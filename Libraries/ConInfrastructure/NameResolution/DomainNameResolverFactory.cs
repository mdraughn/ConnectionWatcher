using System.Net;
using ConCore;

namespace ConInfrastructure.NameResolution
{
    public class DomainNameResolverFactory : IDomainNameResolverFactory
    {
        public IDomainNameResolver Create(CancellationToken cancellationToken)
        {
            return new DomainNameResolverDns(cancellationToken);
        }   
    }
}
