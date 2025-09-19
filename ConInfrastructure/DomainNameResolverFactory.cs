using System.Net;
using ConEngine.Network;

namespace ConInfrastructure
{
    public class DomainNameResolverFactory : IDomainNameResolverFactory
    {
        public IDomainNameResolver Create(CancellationToken cancellationToken)
        {
            return new DomainNameResolverDns(cancellationToken);
        }   
    }
}
