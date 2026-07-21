using Android.App;
using Android.Content;

namespace Drumless.Mobile;

internal static class DrumlessTaskForeground
{
    public static void BringToFront()
    {
        try
        {
            var context = Android.App.Application.Context;
            var manager = (ActivityManager?)context.GetSystemService(Context.ActivityService);

            // AppTask is scoped to this application, so prefer it over launching a new activity.
            // This restores the existing Drumless task exactly as the user left it before YouTube
            // was opened, preserving the current playlist and UI state.
            var appTask = manager?.AppTasks?.FirstOrDefault();
            if (appTask is not null)
            {
                appTask.MoveToFront();
                return;
            }

            // Fallback for devices/OEMs that do not expose the task through AppTasks.
            var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName);
            if (launchIntent is null)
            {
                return;
            }

            launchIntent.AddFlags(
                ActivityFlags.NewTask |
                ActivityFlags.SingleTop |
                ActivityFlags.ReorderToFront);
            context.StartActivity(launchIntent);
        }
        catch (Exception)
        {
            // Returning to the UI is best-effort. Playlist sequencing must continue even if
            // Android or the device manufacturer blocks a programmatic foreground transition.
        }
    }
}
