using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Finansist.Core.Finance;

namespace Finansist.Core.Importing;

public static class VbrrPushParser
{
    private const string Number = @"[0-9]+(?:[ \u00a0\u202f][0-9]{3})*(?:[.,][0-9]{1,2})?";
    private static readonly Regex Operation = new(@"(?<!\p{L})(?<kind>пополнение|popolnenie|оплата|oplata|списание|spisanie)(?!\p{L})\s*(?<sbp>СБП|SBP)?\s*(?<amount>" + Number + @")\s*(?:₽|руб\.?|RUB|р\.?|R)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OperationWord = new(@"(?<!\p{L})(пополнение|popolnenie|оплата|oplata|списание|spisanie)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Time = new(@"\b(?<hour>[0-2]?[0-9]):(?<minute>[0-5][0-9])\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Sensitive = new(@"код\s+(?:подтверждения|для|входа)|kod\s+(?:podtverzhden|dlya|vhoda)|одноразов|пароль|parol|password|verification|\bOTP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static ParsedBankPush? Parse(BankPush push)
    {
        var title = push.Title.Trim();
        var text = title.Length > 0 && push.Text.Contains(title, StringComparison.OrdinalIgnoreCase) ? push.Text.Trim() : (title + "\n" + push.Text).Trim();
        if (text.Length > 8000 || push.IsGroupSummary || Sensitive.IsMatch(text)) return null;
        var word = OperationWord.Match(text);
        if (!word.Success) return null; // Advertisements and authentication messages are not copied to the queue.
        var match = Operation.Match(text);
        var kindWord = (match.Success ? match.Groups["kind"].Value : word.Value).ToLowerInvariant();
        var income = kindWord is "пополнение" or "popolnenie";
        var ambiguous = kindWord is "списание" or "spisanie";
        var multiple = Operation.Matches(text).Count > 1;
        long? amount = null;
        if (match.Success && !multiple)
        {
            var input = match.Groups["amount"].Value.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "");
            try { amount = Money.Parse(input); } catch (FinanceValidationException) { }
        }
        var posted = push.PostedAt.ToLocalTime();
        var time = Time.Match(text, match.Success ? match.Index + match.Length : word.Index + word.Length);
        string? messageTime = time.Success && int.Parse(time.Groups["hour"].Value) <= 23 ? time.Value : null;
        var notice = "Дата предложена по времени получения push. Проверьте её перед подтверждением.";
        if (ambiguous) notice += "\nСписание может быть переводом между своими счетами. Выберите тип операции.";
        if (amount is null) notice += "\nСумма не распознана — введите её вручную.";
        if (multiple) notice += "\nВ уведомлении несколько операций. Автоматическая сумма не подставлена; проверьте текст и внесите операции по отдельности.";
        var description = income ? "Поступление ВБРР" : ambiguous ? "Списание ВБРР" : "Оплата ВБРР";
        if (match.Success && !income && !ambiguous)
        {
            var tail = text[(match.Index + match.Length)..];
            var balance = Regex.Match(tail, @"(?:Баланс|Balans)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (balance.Success) tail = tail[..balance.Index];
            tail = Time.Replace(tail, "").Trim();
            if (!string.IsNullOrWhiteSpace(tail)) description = tail[..Math.Min(300, tail.Length)];
        }
        return new(income ? OperationKind.Income : OperationKind.Expense, amount, DateOnly.FromDateTime(posted.DateTime),
            messageTime, description, ambiguous, notice);
    }
    public static string Fingerprint(BankPush push) => Hash(push.PackageId + "\n" + Whitespace.Replace((push.Title + "\n" + push.Text).Trim(), " ").ToUpperInvariant());
    public static string EventKey(BankPush push) => Hash(push.PackageId + "\n" + push.Key + "\n" + push.PostedAt.ToUnixTimeMilliseconds() + "\n" + Fingerprint(push));
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
