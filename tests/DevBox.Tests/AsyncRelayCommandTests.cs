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
}
