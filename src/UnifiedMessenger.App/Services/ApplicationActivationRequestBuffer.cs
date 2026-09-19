namespace UnifiedMessenger.App.Services;

internal sealed class ApplicationActivationRequestBuffer
{
    private readonly object _gate = new();
    private Action? _activationTarget;
    private bool _activationPending;

    internal void RequestActivation()
    {
        Action? activationTarget;
        lock (_gate)
        {
            activationTarget = _activationTarget;
            if (activationTarget is null)
            {
                _activationPending = true;
                return;
            }
        }

        activationTarget();
    }

    internal void SetTarget(Action activationTarget)
    {
        ArgumentNullException.ThrowIfNull(activationTarget);
        bool activatePending;
        lock (_gate)
        {
            _activationTarget = activationTarget;
            activatePending = _activationPending;
            _activationPending = false;
        }

        if (activatePending)
        {
            activationTarget();
        }
    }

    internal void ClearTarget()
    {
        lock (_gate)
        {
            _activationTarget = null;
            _activationPending = false;
        }
    }
}
