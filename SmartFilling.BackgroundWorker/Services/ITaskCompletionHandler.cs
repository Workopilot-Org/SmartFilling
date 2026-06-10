using SmartFilling.BackgroundWorker.Models;

namespace SmartFilling.BackgroundWorker.Services;

public interface ITaskCompletionHandler
{
    Task OnTaskCompleted(FillTask task);
}
