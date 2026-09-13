using System;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// Holds the transition gate open for as long as it is alive. Created by
    /// <c>EnemyBehavior.AuthoritativeScope</c>.
    /// </summary>
    /// <remarks>
    /// A thin wrapper rather than exposing the gate itself, so consumers depend on the public
    /// facade and the gate's internals stay free to change.
    /// </remarks>
    internal sealed class AuthoritativeToken : IDisposable
    {
        private readonly IDisposable _inner = FsmTransitionGate.Open();

        public void Dispose() => _inner.Dispose();
    }
}
