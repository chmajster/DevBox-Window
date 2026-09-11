namespace DevBox.Core.Services;

internal static class RollbackExecutor
{
    public static void RethrowAfterRollback(Exception original, params Action[] rollbackActions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rollbackActions);

        var rollbackErrors = new List<Exception>();
        foreach (var action in rollbackActions)
        {
            if (action is null)
                continue;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                rollbackErrors.Add(ex);
            }
        }

        if (rollbackErrors.Count > 0)
        {
            throw new AggregateException(
                "The operation failed and one or more rollback operations also failed.",
                new[] { original }.Concat(rollbackErrors));
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
    }
}
