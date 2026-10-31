// SlopTank modification notice: added or changed by SlopTank on 2026-10-12, 2026-10-14, 2026-10-31.
using System;

namespace Jellyfin.Server.Implementations.Users;

internal interface IUserLockWaitObserver
{
    void OnWaitingForKey(Guid key);
}
