namespace KfdMobile;

// An asynchronous write may only acknowledge the exact container revision it captured.
public sealed record SaveStamp(object Container, long Revision)
{
    public bool Matches(object container, long revision) => ReferenceEquals(Container, container) && Revision == revision;
}
