using Android.App;
using Android.Content;

namespace Drumless.Mobile;

internal static class DrumlessTaskForeground
{
    public static bool BringToFront()
    {
        try
        {
            var activity = MainActivity.Current;
            if (activity is null || activity.IsFinishing || activity.IsDestroyed)
            {
                return false;
            }

            var manager = (ActivityManager?)activity.GetSystemService(Context.ActivityService);
            if (manager is null)
            {
                return false;
            }

            // Use the exact task that owns the live MAUI activity. The previous AppTasks-based
            // implementation could select/relaunch a stale task on some devices. Here we only
            // reorder Drumless' current task; no new MainActivity instance is created.
            manager.MoveTaskToFront(activity.TaskId, (MoveTaskFlags)0);
            return true;
        }
        catch (Exception)
        {
            // Returning to the UI is best-effort. Playlist sequencing must keep running even if
            // Android refuses to reorder the task in the current background state.
            return false;
        }
    }
}
