using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One article of the in-game handbook.</summary>
public sealed class HandbookArticle
{
    public string Id = "";
    public string Topic = "";
    public int Order;
    /// <summary>The screen the article is about, opened by its "Open" button; empty for none.</summary>
    public string Screen = "";
    /// <summary>Other articles worth reading next.</summary>
    public string[] Related = Array.Empty<string>();

    public string Title => Loc.GetOr($"handbook.{Id}.title", Id);
    public string Body => Loc.GetOr($"handbook.{Id}.body", "");
}

public sealed class HandbookTopic
{
    public string Id = "";
    public int Order;
    public string Name => Loc.GetOr($"handbook.topic.{Id}", Id);
}

/// <summary>
/// The handbook: what each part of the game is, what its figures mean and what to do about
/// them, inside the game rather than in a document beside it.
///
/// The structure is data - common/handbook - and the words are localisation:
/// `handbook.&lt;id&gt;.title` and `handbook.&lt;id&gt;.body`, `handbook.topic.&lt;id&gt;`. A body is
/// paragraphs separated by blank lines; a line that starts with "## " is a heading and one
/// that starts with "- " is a point. A mod adds an article by naming a new id with its topic,
/// and writes its text in its own localisation files.
/// </summary>
public static class HandbookCatalog
{
    private static readonly Dictionary<string, HandbookArticle> Articles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HandbookTopic> Topics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ScreenArticles = new(StringComparer.OrdinalIgnoreCase);

    static HandbookCatalog() => ResetCatalog();

    public static IReadOnlyList<HandbookTopic> OrderedTopics =>
        Topics.Values.OrderBy(topic => topic.Order).ThenBy(topic => topic.Id, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<HandbookArticle> In(string topic) =>
        Articles.Values.Where(article => string.Equals(article.Topic, topic, StringComparison.OrdinalIgnoreCase))
            .OrderBy(article => article.Order).ThenBy(article => article.Id, StringComparer.Ordinal).ToArray();

    public static HandbookArticle Find(string id) =>
        id is not null && Articles.TryGetValue(id, out var article) ? article : null;

    /// <summary>The article that explains a screen, or the first one if no article claims it.</summary>
    public static string ForScreen(string screen) =>
        screen is not null && ScreenArticles.TryGetValue(screen, out var id) ? id : "";

    public static int Count => Articles.Count;

    public static void ResetCatalog()
    {
        Articles.Clear();
        Topics.Clear();
        ScreenArticles.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "handbook"))
            using (document)
                Merge(document.RootElement, null);
    }

    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Object)
            foreach (var entry in topics.EnumerateObject())
            {
                if (!Topics.TryGetValue(entry.Name, out var topic)) Topics[entry.Name] = topic = new HandbookTopic { Id = entry.Name };
                if (entry.Value.TryGetInt32(out var order)) topic.Order = order;
                else if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("order", out var nested) &&
                         nested.TryGetInt32(out var nestedOrder)) topic.Order = nestedOrder;
            }
        if (!root.TryGetProperty("articles", out var articles) || articles.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in articles.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (entry.Value.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.True)
            {
                Articles.Remove(entry.Name);
                continue;
            }
            if (!Articles.TryGetValue(entry.Name, out var article)) Articles[entry.Name] = article = new HandbookArticle { Id = entry.Name };
            foreach (var field in entry.Value.EnumerateObject())
                switch (field.Name)
                {
                    case "topic": article.Topic = field.Value.GetString() ?? ""; break;
                    case "order" when field.Value.TryGetInt32(out var order): article.Order = order; break;
                    case "screen": article.Screen = field.Value.GetString() ?? ""; break;
                    case "related" when field.Value.ValueKind == JsonValueKind.Array:
                        article.Related = field.Value.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString()).ToArray();
                        break;
                    default: errors?.Add($"common/handbook: '{entry.Name}.{field.Name}' is not something this game reads."); break;
                }
            if (article.Topic.Length > 0 && !Topics.ContainsKey(article.Topic))
                Topics[article.Topic] = new HandbookTopic { Id = article.Topic, Order = 100 };
            if (article.Screen.Length > 0 && !ScreenArticles.ContainsKey(article.Screen)) ScreenArticles[article.Screen] = article.Id;
        }
    }

    /// <summary>Articles whose title or text mentions every word of the query.</summary>
    public static IEnumerable<HandbookArticle> Search(string query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var topic in OrderedTopics)
        foreach (var article in In(topic.Id))
        {
            if (words.Length == 0) { yield return article; continue; }
            var text = article.Title + " " + article.Body;
            if (words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase))) yield return article;
        }
    }
}
