using System.Text;

namespace Finansist.Core.Shopping;

public sealed record ProductSearchResult(IReadOnlyList<Product> Matches, IReadOnlyList<Product> Similar);

// Local name matching only. Suggestions are lexical, never semantic or AI generated.
public sealed class ProductSearchIndex
{
    private sealed record Entry(Product Product, string Normalized, string[] Words);
    private readonly Entry[] entries;
    public ProductSearchIndex(IEnumerable<Product> products) => entries = products
        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal)
        .Select(p => { var name = Normalize(p.Name); return new Entry(p, name, name.Split(' ', StringSplitOptions.RemoveEmptyEntries)); }).ToArray();
    public ProductSearchResult Find(string? query)
    {
        var normalized = Normalize((query ?? "").Length > 80 ? query![..80] : query ?? "");
        if (normalized.Length == 0) return new(entries.Select(e => e.Product).ToArray(), []);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static int Rank(Entry entry, string query, string[] words) => entry.Normalized == query ? 0 : entry.Normalized.StartsWith(query, StringComparison.Ordinal) ? 1
            : entry.Normalized.Contains(query, StringComparison.Ordinal) ? 2 : words.All(w => entry.Normalized.Contains(w, StringComparison.Ordinal)) ? 3 : -1;
        var matches = entries.Select(e => (Entry: e, Rank: Rank(e, normalized, words))).Where(e => e.Rank >= 0).OrderBy(e => e.Rank).ToArray();
        var matchedIds = matches.Select(e => e.Entry.Product.Id).ToHashSet();
        var searchWords = words.Where(w => w.Length >= 3 && w.Any(char.IsLetter)).ToArray();
        var similar = searchWords.Length == 0 ? [] : entries.Where(e => !matchedIds.Contains(e.Product.Id))
            .Select(e => (Entry: e, Score: Similarity(searchWords, e.Words))).Where(e => e.Score < int.MaxValue)
            .OrderBy(e => e.Score).Take(10).Select(e => e.Entry.Product).ToArray();
        return new(matches.Select(e => e.Entry.Product).ToArray(), similar);
    }
    private static int Similarity(string[] query, string[] words)
    {
        var best = int.MaxValue;
        foreach (var q in query)
            foreach (var word in words.Where(w => w.Length >= 3))
            {
                var limit = q.Length < 6 ? 1 : 2;
                var distance = Distance(q, word, limit);
                if (distance <= limit) best = Math.Min(best, distance * 100);
                var prefix = 0;
                while (prefix < Math.Min(q.Length, word.Length) && q[prefix] == word[prefix]) prefix++;
                if (prefix >= 4 && prefix * 100 >= Math.Min(q.Length, word.Length) * 60)
                    best = Math.Min(best, 250 + Math.Abs(q.Length - word.Length));
            }
        return best;
    }
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i; var minimum = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                minimum = Math.Min(minimum, current[j]);
            }
            if (minimum > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
    private static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(c)) result.Append(c);
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }
}
