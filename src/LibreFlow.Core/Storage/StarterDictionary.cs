namespace LibreFlow.Core.Storage;

public sealed record StarterWord(string Phrase, string? ReplaceFrom = null);

/// <summary>
/// Names of tools and tech terms that speech models routinely get wrong in Spanish.
/// Every new installation starts with them in the dictionary, as suggestions the user can edit or delete.
///
/// Order matters: the speech model only receives the first few hundred characters of the dictionary
/// as a hint, so the most common words go first. A misheard form (ReplaceFrom) must never be a real
/// word, because it is replaced wherever it shows up; the tests enforce that.
/// </summary>
public static class StarterDictionary
{
    /// <summary>Raise it when adding words, so existing installations receive the new ones.</summary>
    public const int Version = 1;

    public static IReadOnlyList<StarterWord> Words { get; } =
    [
        // Assistants and models
        new("Claude"),
        new("Claude Code", "clod code, cloud code, claude cod"),
        new("ChatGPT", "chat gpt, chat gepeté, chatgepeté"),
        new("Anthropic", "antropic, antrópic, anthropics"),
        new("OpenAI", "open ai, open ia, openia"),
        new("Gemini"),
        new("Copilot", "copailot"),
        new("Perplexity", "perplexiti, perpléxiti"),
        new("Whisper", "wisper, güisper"),
        new("Groq"),
        new("Sonnet", "sonet"),
        new("Opus"),
        new("Haiku", "jaiku"),
        new("Codex"),
        new("Midjourney", "mid journey, midyorni"),
        new("ElevenLabs", "eleven labs, ileven labs"),

        // Everyday AI and automation vocabulary
        new("prompt", "promt, pront, promp"),
        new("prompts", "promts, pronts, promps"),
        new("MCP", "eme ce pe, m c p"),
        new("LLM", "ele ele eme"),
        new("n8n", "n ocho n, ene ocho ene, n 8 n, ene 8 ene"),
        new("skill", "eskil, esquil"),
        new("skills", "eskils, esquils"),
        new("API key", "api qui, apiki"),
        new("webhook", "web hook"),
        new("workflow", "work flow, worflow"),
        new("workflows", "work flows, worflows"),
        new("dashboard", "dashbord, dash board"),

        // Development
        new("GitHub", "git hub, guithub"),
        new("Vercel", "bercel, versel, vercell"),
        new("Supabase", "supa base, supabeis"),
        new("Next.js", "next js, nextjs"),
        new("Node.js", "node js, nodejs"),
        new("React"),
        new("TypeScript", "type script, taipscript"),
        new("JavaScript", "java script, yavascript"),
        new("Python", "paiton, páiton"),
        new("Docker", "doker, dóker"),
        new("Kubernetes", "kubernetis, cubernetes"),
        new("PostgreSQL", "postgres sql, postgre sql"),
        new("Tailwind", "tail wind, teilwind"),
        new("Cloudflare", "cloud flare, claudfler"),
        new("Firebase", "fire base, fairbeis"),
        new("Stripe", "straip"),

        // Tools
        new("Zapier", "zápier, sapier"),
        new("Airtable", "air table, ertable"),
        new("Notion"),
        new("Slack", "eslac, eslack"),
        new("Figma"),
        new("Canva"),
        new("ManyChat", "many chat, mani chat, menichat"),
        new("Zernio", "sernio, cernio, zernío"),
        new("Apify", "apifai, api fy, apifay"),
        new("Remotion"),
        new("Higgsfield", "higsfield, hicksfield, higs field, higgs field"),
        new("Shopify", "shopifai, chopify"),
        new("WordPress", "word press"),
        new("HubSpot", "hub spot, jabspot"),
        new("Calendly", "calendli, cálendli"),
        new("Google Meet", "google mit"),
        new("Zoom"),

        // Social media and business
        new("WhatsApp", "wasap, guasap, whats app, whatsap"),
        new("Instagram"),
        new("TikTok", "tik tok"),
        new("YouTube", "you tube, yutub"),
        new("LinkedIn", "linked in, linkedín, linquedin"),
        new("reel", "ril"),
        new("reels", "rils"),
        new("carrusel", "carrousel"),
        new("CRM"),
        new("SaaS"),
        new("Mercado Pago", "mercadopago"),
        new("Mercado Libre", "mercadolibre"),
        new("Libre Flow", "libre flou, libreflow, libre flo"),
    ];
}
