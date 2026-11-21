package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * This phone's own notifications, for the apps the panel mirrors, pushed to the computer over the paired link.
 *
 * The widget used to read them with `dumpsys` over adb, which needs a cable. A notification listener runs on the
 * phone itself, so the source phone only has to be reachable over Wi-Fi. Only the allow-listed packages are read;
 * nothing is written to disk and nothing is logged: the list exists for the moment it takes to send it.
 *
 * Android binds this service once the owner grants notification access (see [allowed]); it does not need the app
 * to be open. A change sends a snapshot, and a slow heartbeat keeps the agent from treating the phone as gone.
 */
class NotificationRelay : NotificationListenerService() {
    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private val queued = AtomicBoolean(false)
    @Volatile private var link: AgentLink? = null

    private val flush = Runnable { flushNow() }
    private val heartbeat = object : Runnable {
        override fun run() {
            queue()
            main.postDelayed(this, HEARTBEAT_MS)
        }
    }

    override fun onCreate() {
        super.onCreate()
        link = AgentLink(applicationContext)
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        queue()
        main.removeCallbacks(heartbeat)
        main.postDelayed(heartbeat, HEARTBEAT_MS)
    }

    /**
     * Access was withdrawn (or the system unbound us): stop the heartbeat instead of pretending to still be the
     * source. The agent sees the silence and shows the phone as disconnected, which is the truth.
     */
    override fun onListenerDisconnected() {
        main.removeCallbacks(flush)
        main.removeCallbacks(heartbeat)
        queued.set(false)
        super.onListenerDisconnected()
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        link?.close()
        link = null
        io.shutdown()
        super.onDestroy()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn != null && APP_BY_PACKAGE.containsKey(sbn.packageName)) queue()
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        if (sbn != null && APP_BY_PACKAGE.containsKey(sbn.packageName)) queue()
    }

    /** A burst of posts (a group update) sends one snapshot, not one per notification. */
    private fun queue() {
        if (!queued.compareAndSet(false, true)) return
        main.removeCallbacks(flush)
        main.postDelayed(flush, BURST_MS)
    }

    private fun flushNow() {
        // the service can be torn down while a burst is still queued; a late submit must not crash the app
        try {
            io.execute {
                queued.set(false)
                // offline, asleep or unpaired: the next change or heartbeat tries again
                try { send() } catch (_: Exception) { }
            }
        } catch (_: Exception) {
            queued.set(false)
        }
    }

    private fun send() {
        val l = link ?: return
        val active = try { activeNotifications } catch (_: Exception) { null } ?: return
        val items = JSONArray()
        for (sbn in active) {
            val app = APP_BY_PACKAGE[sbn.packageName] ?: continue
            val n = sbn.notification ?: continue
            if (n.flags and FLAG_GROUP_SUMMARY != 0) continue
            val ex = n.extras ?: continue
            val sender = oneLine(ex.getCharSequence(Notification.EXTRA_TITLE), MAX_SENDER)
            val text = oneLine(
                ex.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: ex.getCharSequence(Notification.EXTRA_TEXT), MAX_TEXT,
            )
            if (sender.isEmpty() && text.isEmpty()) continue
            val key = sbn.key ?: continue
            items.put(
                JSONObject()
                    .put("id", key)
                    .put("app", app)
                    .put("package", sbn.packageName)
                    .put("sender", sender)
                    .put("text", text)
                    .put("at", if (sbn.postTime > 0) sbn.postTime else System.currentTimeMillis()),
            )
        }
        val body = JSONObject().put("items", items).toString().toByteArray()
        l.request("POST", "/api/mirror/push", body, "application/json")
    }

    private fun oneLine(value: CharSequence?, max: Int): String {
        val s = (value ?: "").toString().replace(WHITESPACE, " ").trim()
        return if (s.length <= max) s else s.substring(0, max - 1) + "\u2026"
    }

    companion object {
        /** The only packages mirrored, with the names the panel shows; the agent ignores anything else. */
        private val APP_BY_PACKAGE = mapOf(
            "com.facebook.orca" to "Messenger",
            "com.discord" to "Discord",
            "com.microsoft.teams" to "Teams",
            "com.instagram.android" to "Instagram",
        )
        private val WHITESPACE = Regex("\\s+")
        private const val FLAG_GROUP_SUMMARY = 0x200
        private const val MAX_SENDER = 80
        private const val MAX_TEXT = 200
        private const val BURST_MS = 250L
        private const val HEARTBEAT_MS = 30_000L

        /** True when this phone has granted the app notification access. */
        fun allowed(context: Context): Boolean {
            val flat = Settings.Secure.getString(context.contentResolver, "enabled_notification_listeners") ?: return false
            return flat.split(':').any { ComponentName.unflattenFromString(it)?.packageName == context.packageName }
        }
    }
}
