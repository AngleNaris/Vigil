namespace SystemAutorun.Core.Models;

// Missing task fields inherit the previous policy; explicit task choices always win.
// These are scheduling choices, not target identity or durable recovery intent.
public static class TaskAutomationPolicy
{
    public static bool StartsOnAppLaunch(AutorunTask task, ApplicationSettings preferences) =>
        task.StartOnAppLaunch ?? preferences.AutoStartMissingOnLaunch;

    public static bool Recovers(AutorunTask task, ApplicationSettings preferences) =>
        task.AutoRecover ?? (preferences.AutoRecoverFailedTasks && task.ExpectedRunning);

    public static bool StartsOnAppLaunch(TaskSnapshot task, ApplicationSettings preferences) =>
        task.StartOnAppLaunch ?? preferences.AutoStartMissingOnLaunch;

    public static bool Recovers(TaskSnapshot task, ApplicationSettings preferences) =>
        task.AutoRecover ?? (preferences.AutoRecoverFailedTasks && task.ExpectedRunning);
}
