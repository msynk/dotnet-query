using System.Windows.Input;

namespace DotNetQuery.Mvvm;

// The ICommand behind the bindable commands of ObservableQuery, ObservableInfiniteQuery and ObservableMutation.
// Execution is fire-and-forget (a command has no caller to return a task to); failures are already surfaced through
// the bound Error/IsError state, so they are observed here instead of crashing the UI thread.
internal sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _ = ObserveAsync(execute(parameter));
        }
    }

    // Called on the UI thread by the owner when a property CanExecute depends on changed.
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Surfaced through the bound state.
        }
    }
}
