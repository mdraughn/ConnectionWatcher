namespace ConCore;

public interface IDomainNameResolverFactory
{
    IDomainNameResolver Create(CancellationToken cancellationToken);
}