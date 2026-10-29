using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine;
using QuantConnect.Lean.Engine.Server;
using QuantConnect.Packets;

namespace Stockhog.EngineRunner;

/// <summary>
/// Local runs have no QuantConnect cloud project to manage, so every hook is a no-op.
/// </summary>
internal sealed class NullLeanManager : ILeanManager
{
    public void Initialize(IAlgorithm algorithm, AlgorithmNodePacket job, IApi api, CancellationToken cancellationToken)
    {
    }

    public void Initialize(LeanEngineSystemHandlers systemHandlers, LeanEngineAlgorithmHandlers algorithmHandlers,
        AlgorithmNodePacket job, AlgorithmManager algorithmManager)
    {
    }

    public void SetAlgorithm(IAlgorithm algorithm)
    {
    }

    public void Update()
    {
    }

    public void OnAlgorithmStart()
    {
    }

    public void OnAlgorithmEnd()
    {
    }

    public void OnSecuritiesChanged(SecurityChanges changes)
    {
    }

    public void Dispose()
    {
    }
}
