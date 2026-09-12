using System.Diagnostics;
using System.Windows.Input;

namespace DevBox.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        if (CanExecuteChanged is null)
            return;
        foreach (EventHandler handler in CanExecuteChanged.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"RelayCommand CanExecuteChanged subscriber failed: {ex}");
            }
        }
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unhandled asynchronous command exception: {ex}");
            RaiseExecutionFailed(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public event Action<Exception>? ExecutionFailed;

    public void RaiseCanExecuteChanged()
    {
        if (CanExecuteChanged is null)
            return;
        foreach (EventHandler handler in CanExecuteChanged.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"AsyncRelayCommand CanExecuteChanged subscriber failed: {ex}");
            }
        }
    }

    private void RaiseExecutionFailed(Exception exception)
    {
        if (ExecutionFailed is null)
            return;
        foreach (Action<Exception> handler in ExecutionFailed.GetInvocationList())
        {
            try
            {
                handler(exception);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"AsyncRelayCommand ExecutionFailed subscriber failed: {ex}");
            }
        }
    }
}
