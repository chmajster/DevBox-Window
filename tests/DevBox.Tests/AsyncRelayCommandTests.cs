using DevBox.App.ViewModels;
using Xunit;

namespace DevBox.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_FaultedAction_IsReportedWithoutEscapingToDispatcher()
    {
        var expected = new InvalidOperationException("boom");
        Exception? observed = null;
        var command = new AsyncRelayCommand(() => Task.FromException(expected));
        command.ExecutionFailed += exception => observed = exception;

        await command.ExecuteAsync(null);

        Assert.Same(expected, observed);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsync_DisablesCommandUntilActionCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            started.SetResult();
            await release.Task;
        });

        var execution = command.ExecuteAsync(null);
        await started.Task;

        Assert.False(command.CanExecute(null));

        release.SetResult();
        await execution;

        Assert.True(command.CanExecute(null));
    }
    [Fact]
    public async Task ExecuteAsync_ThrowingEventSubscribers_DoNotFaultOrLeaveCommandDisabled()
    {
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("action failed")));
        var canExecuteNotifications = 0;
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("can-execute subscriber failed");
        command.CanExecuteChanged += (_, _) => canExecuteNotifications++;
        command.ExecutionFailed += _ => throw new InvalidOperationException("failure subscriber failed");

        await command.ExecuteAsync(null);

        Assert.True(command.CanExecute(null));
        Assert.Equal(2, canExecuteNotifications);
    }

    [Fact]
    public void RelayCommand_ThrowingCanExecuteSubscriber_DoesNotEscape()
    {
        var command = new RelayCommand(() => { });
        var observed = 0;
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        command.CanExecuteChanged += (_, _) => observed++;

        command.RaiseCanExecuteChanged();

        Assert.Equal(1, observed);
    }

}
