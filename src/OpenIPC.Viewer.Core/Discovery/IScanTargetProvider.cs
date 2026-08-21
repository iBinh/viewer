using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Discovery;

// Where "which subnets could hold cameras?" gets answered without asking the
// user. Implementations read the OS routing table where they can and fall back
// to the local interface subnets where they cannot (macOS, mobile).
//
// Cheap and synchronous: it inspects local OS state, it does not touch the
// network.
public interface IScanTargetProvider
{
    IReadOnlyList<ScanTarget> GetTargets();
}
