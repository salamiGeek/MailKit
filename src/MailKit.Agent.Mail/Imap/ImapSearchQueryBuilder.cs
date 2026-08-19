using MailKit.Agent.Core.Mail;
using MailKit.Search;

namespace MailKit.Agent.Mail.Imap;

public static class ImapSearchQueryBuilder
{
    public static SearchQuery Build(MessageSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        SearchQuery? query = null;
        Add(ref query, criteria.Text, SearchQuery.MessageContains);
        Add(ref query, criteria.From, SearchQuery.FromContains);
        Add(ref query, criteria.To, SearchQuery.ToContains);
        Add(ref query, criteria.Subject, SearchQuery.SubjectContains);

        if (criteria.Since.HasValue)
            Combine(ref query, SearchQuery.DeliveredAfter(criteria.Since.Value.Date));
        if (criteria.Before.HasValue)
            Combine(ref query, SearchQuery.DeliveredBefore(criteria.Before.Value.Date));
        if (criteria.Unread.HasValue)
            Combine(ref query, criteria.Unread.Value ? SearchQuery.NotSeen : SearchQuery.Seen);

        return query ?? SearchQuery.All;
    }

    private static void Add(
        ref SearchQuery? query,
        string? value,
        Func<string, SearchQuery> factory)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Combine(ref query, factory(value));
    }

    private static void Combine(ref SearchQuery? query, SearchQuery next) =>
        query = query is null ? next : query.And(next);
}
