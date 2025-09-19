using System.Net;

namespace ConEngine.Network;

public interface IDomainNameResolverFactory
{
    IDomainNameResolver Create(CancellationToken cancellationToken);
}