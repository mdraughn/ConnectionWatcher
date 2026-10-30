namespace ConCore.Dns;

/// <summary>The outcome words of a DNS probe message: the leading token of
/// "Filtered: NXDOMAIN (dns.google)". The transceiver writes them; the result log,
/// the verdict notes and the status read them.</summary>
public static class DnsOutcome
{
    public const string Verified = "Verified";
    public const string Answered = "Answered";
    public const string Filtered = "Filtered";
    public const string WrongAnswer = "WrongAnswer";

    /// <summary>A seeded-name outcome judged against its expected set (Answered is unjudged).</summary>
    public static bool IsJudged(string outcome) => outcome is Verified or Filtered or WrongAnswer;
}
