using System.Net;
using ConCore.Dns;

namespace ConInfrastructure.Dns;

/// <summary>
/// The live lookup-name list the DNS transceivers read. The host replaces it
/// before every engine start (settings + hostname targets); each resolver
/// address keeps its own cursor so two resolvers are not asked the same name
/// at the same moment.
/// </summary>
public sealed class DnsLookupNames : IDnsLookupNames
{
    public static DnsLookupNames Empty { get; } = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, int> _cursor = new();
    private IReadOnlyList<DnsLookupName> _names = [];

    public IReadOnlyList<DnsLookupName> Current
    {
        get { lock (_lock) return _names; }
    }

    public void Replace(IReadOnlyList<DnsLookupName> names)
    {
        lock (_lock)
            _names = names.ToList();
    }

    public DnsLookupName? Next(IPAddress resolver)
    {
        lock (_lock)
        {
            if (_names.Count == 0)
                return null;
            var i = _cursor.GetValueOrDefault(resolver) % _names.Count;
            _cursor[resolver] = i + 1;
            return _names[i];
        }
    }
}
