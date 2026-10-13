namespace Echo.App.Engine;

/// <summary>
/// Generates Echo's localized help page (FR/EN). Tabbed layout aligned with the app
/// (navy/accent, light/dark). Every app view shown here is a faithful, anonymous
/// HTML/CSS mock-up (no client data, no real screenshots), so the produced HTML is
/// fully self-contained and renders in both light and dark themes. <see cref="Build"/>
/// returns the HTML string; the bridge writes it to
/// <c>%LOCALAPPDATA%\Echo\Echo - Aide (xx).html</c> and opens it.
/// </summary>
public static class HelpPage
{
    private const string MistralUrl = "https://console.mistral.ai/home";

    public static string Build(string lang = "fr", string version = "5.0.1")
    {
        lang = lang == "en" ? "en" : "fr";
        bool en = lang == "en";
        var body = en ? BodyEn : BodyFr;

        body = body
            .Replace("__LOGO__", Logo).Replace("__ARROW__", ArrowSvg)
            .Replace("__INFO__", InfoSvg).Replace("__WARN__", WarnSvg).Replace("__CHECKI__", CheckSvg)
            .Replace("__TAB_START__", Ti(IStart)).Replace("__TAB_REC__", Ti(IRec))
            .Replace("__TAB_AFTER__", Ti(IAfter)).Replace("__TAB_KEY__", Ti(IKey))
            .Replace("__TAB_HELP__", Ti(IHelp))
            .Replace("__MURL__", MistralUrl)
            .Replace("__MK1__", MkMistral(lang)).Replace("__MK2__", MkDialog(lang))
            .Replace("__MK3__", MkSuccess(lang))
            .Replace("__M_HOME__", MockHome(en))
            .Replace("__M_TR__", MockTranscript(en))
            .Replace("__M_CR__", MockCr(en))
            .Replace("__M_PART__", MockParticipants(en))
            .Replace("__M_SHARE__", MockShare(en))
            .Replace("__M_WIDGET__", MockWidget(en, false))
            .Replace("__M_WNOTES__", MockWidget(en, true))
            .Replace("__M_APIKEY__", MockApi(en))
            .Replace("{version}", version);

        var title = en ? "Echo, Help" : "Echo, Aide";
        return "<!DOCTYPE html>\n<html lang=\"" + lang + "\"><head><meta charset=\"utf-8\">"
             + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
             + "<title>" + title + "</title><style>" + Css + "</style></head><body>"
             + body + "<script>" + Js + "</script></body></html>";
    }

    // --- petites icones / svg ------------------------------------------------
    private const string Logo = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.2\" stroke-linecap=\"round\"><path d=\"M4 12h1M8 8v8M12 4v16M16 8v8M20 12h-1\"/></svg>";
    private const string CheckSvg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.6\" stroke-linecap=\"round\"><path d=\"M5 12l5 5 9-11\"/></svg>";
    private const string ArrowSvg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"><path d=\"M5 12h14M13 6l6 6-6 6\"/></svg>";
    private const string InfoSvg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"><circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 8v.01M11 12h1v4h1\"/></svg>";
    private const string WarnSvg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"><path d=\"M12 3l9 16H3z\"/><path d=\"M12 10v4M12 17v.01\"/></svg>";

    private const string IStart = "<path d=\"M3 11l9-8 9 8\"/><path d=\"M5 10v10h14V10\"/>";
    private const string IRec = "<path d=\"M12 2a4 4 0 0 1 4 4v5a4 4 0 0 1-8 0V6a4 4 0 0 1 4-4z\"/><path d=\"M5 11a7 7 0 0 0 14 0M12 18v3\"/>";
    private const string IAfter = "<path d=\"M6 3h9l4 4v14H6z\"/><path d=\"M9 13l2 2 4-4\"/>";
    private const string IKey = "<circle cx=\"8\" cy=\"15\" r=\"4\"/><path d=\"M11 12l7-7 3 3M15 8l2 2\"/>";
    private const string IHelp = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.8.4-1 .8-1 1.7M12 17h.01\"/>";

    // icones utilisees dans les maquettes
    private const string IcList = "<path d=\"M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01\"/>";
    private const string IcImport = "<path d=\"M12 3v12M8 11l4 4 4-4M5 19h14\"/>";
    private const string IcFolder = "<path d=\"M4 7h6l2 2h8v10H4z\"/>";
    private const string IcPlus = "<path d=\"M12 5v14M5 12h14\"/>";
    private const string IcCal = "<rect x=\"4\" y=\"5\" width=\"16\" height=\"16\" rx=\"2\"/><path d=\"M4 9h16M8 3v4M16 3v4\"/>";
    private const string IcGen = "<path d=\"M4 6h16M4 12h16M4 18h10\"/>";
    private const string IcRefresh = "<path d=\"M20 11a8 8 0 1 0-1 4M20 5v6h-6\"/>";
    private const string IcShare = "<path d=\"M12 3v12M8 7l4-4 4 4M5 13v6h14v-6\"/>";
    private const string IcGlobe = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18\"/>";
    private const string IcDoc = "<path d=\"M6 3h9l4 4v14H6z\"/><path d=\"M14 3v5h5\"/>";
    private const string IcImg = "<rect x=\"3\" y=\"6\" width=\"18\" height=\"13\" rx=\"2\"/><circle cx=\"12\" cy=\"12.5\" r=\"3\"/>";
    private const string IcUsers = "<circle cx=\"9\" cy=\"8\" r=\"3\"/><path d=\"M3 20a6 6 0 0 1 12 0M16 6a3 3 0 0 1 0 6M15 20a6 6 0 0 0-1-4\"/>";
    private const string IcNote = "<path d=\"M4 4h11l5 5v11H4z\"/><path d=\"M8 11h8M8 15h5\"/>";
    private const string IcBookmark = "<path d=\"M6 3h12v18l-6-4-6 4z\"/>";
    private const string IcPin = "<path d=\"M9 4h6l-1 7 3 3H7l3-3z\"/><path d=\"M12 17v3\"/>";
    private const string IcChevL = "<path d=\"M15 5l-6 7 6 7\"/>";
    private const string IcClock = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/>";
    private const string IcLink = "<path d=\"M9 15l6-6M8 13l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 11l2-2a3.5 3.5 0 0 0-5-5l-2 2\"/>";
    private const string IcArrowR = "<path d=\"M5 12h12M13 7l5 5-5 5\"/>";
    private const string IcFlag = "<path d=\"M5 4v16M5 4h11l-2 4 2 4H5\"/>";
    private const string IcPencil = "<path d=\"M4 20h4L20 8l-4-4L4 16z\"/>";
    private const string IcCopy = "<rect x=\"8\" y=\"8\" width=\"12\" height=\"12\" rx=\"2\"/><path d=\"M4 16V4h12\"/>";
    private const string IcSearch = "<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"M20 20l-3.5-3.5\"/>";
    private const string IcSun = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4\"/>";
    private const string IcMin = "<path d=\"M5 12h14\"/>";
    private const string IcMax = "<rect x=\"5\" y=\"5\" width=\"14\" height=\"14\" rx=\"2\"/>";
    private const string IcClose = "<path d=\"M6 6l12 12M18 6L6 18\"/>";

    private static string Ti(string path) =>
        "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" + path + "</svg>";

    // ========================================================================
    //  Maquettes de l'application (anonymes, sans capture, thème clair/sombre)
    // ========================================================================
    private static string MTitle(bool en) => en ? "Atlas kick-off meeting" : "Réunion de lancement Atlas";

    private static string Av(string ini, string col) =>
        "<span style=\"background:" + col + "\">" + ini + "</span>";

    // barre de titre + barre latérale, communes à toutes les vues plein écran
    private static string Frame(bool en, string crumb, bool recording, string activeNav, string main, bool rel = false)
    {
        string recBtn = recording
            ? "<div class=\"pbtn rec\"><span class=\"dot\"></span>" + (en ? "Stop recording" : "Arrêter l'enregistrement") + "</div>"
            : "<div class=\"pbtn\"><span class=\"dot\"></span>" + (en ? "New recording" : "Nouvel enregistrement") + "</div>";

        string side =
            recBtn
          + "<div class=\"srow\"><span class=\"sgh\">" + Ti(IcImport) + (en ? "Import" : "Importer") + "</span>"
          + "<span class=\"sgh\">" + Ti(IcFolder) + (en ? "Folder" : "Dossier") + "</span></div>"
          + "<div class=\"anav\">"
          + NavItem(IcHome(), en ? "Home" : "Accueil", activeNav == "home", "")
          + NavItem(IcList, en ? "All meetings" : "Toutes les réunions", activeNav == "all", "5")
          + "</div>"
          + "<div class=\"asec\">" + (en ? "FOLDERS" : "DOSSIERS") + "<span>" + Ti(IcPlus) + "</span></div>"
          + "<div class=\"afold\"><span class=\"fd\" style=\"background:#2f6db0\"></span>Mission Atlas</div>"
          + "<div class=\"afold\"><span class=\"fd\" style=\"background:#3aa35a\"></span>" + (en ? "Internal" : "Interne") + "</div>"
          + "<div class=\"afold\" style=\"margin-top:auto\">" + Ic(IcFolder) + (en ? "Open local folder" : "Ouvrir le dossier local") + "</div>";

        string se = "<span class=\"se\">" + Ic(IcSearch) + (en ? "Search a meeting" : "Rechercher une réunion") + "</span>";
        string wb = "<span class=\"wb\">" + Ti(IcSun) + Ti(IcMin) + Ti(IcMax) + Ti(IcClose) + "</span>";

        return "<div class=\"app\"><div class=\"abar\">"
             + "<span class=\"lg\">" + Logo + "</span><span class=\"nm\">Echo</span>"
             + "<span class=\"cr\">" + crumb + "</span><span class=\"sp\"></span>"
             + se + wb + "</div><div class=\"abody\"><div class=\"aside\">" + side + "</div>"
             + "<div class=\"amain" + (rel ? " rel" : "") + "\">" + main + "</div></div></div>";
    }

    private static string IcHome() => IStart;
    private static string Ic(string p) => "<span class=\"aicn\">" + Ti(p) + "</span>";

    private static string NavItem(string ic, string label, bool on, string count)
    {
        string ct = string.IsNullOrEmpty(count) ? "" : "<span class=\"ct\">" + count + "</span>";
        return "<div class=\"it" + (on ? " on" : "") + "\">" + Ic(ic) + label + ct + "</div>";
    }

    private static string Crumb(bool en, string title) =>
        "<b>" + (en ? "Home" : "Accueil") + "</b> › <b>" + (en ? "Meetings" : "Réunions") + "</b> › " + title;

    // en-tête de fiche réunion + onglets, partagé par plusieurs vues
    private static string MeetHeader(bool en, string tab)
    {
        string avas = "<span class=\"m-ava\">" + Av("CM", "#12a4a4") + Av("TP", "#e8730a") + Av("RB", "#2f6db0")
                    + Av("JF", "#17aed0") + Av("ML", "#3aa35a") + "<span style=\"background:var(--sunken);color:var(--faint)\">+1</span></span>";

        string meta = "<div class=\"m-meta\">" + Ic(IcCal) + (en ? "29 Jun · 3:34 PM · 1:22:50" : "29 juin · 15h34 · 1:22:50")
                    + avas + (en ? "6 attendees" : "6 participants")
                    + "<span class=\"chip ok\">" + (en ? "Minutes" : "Compte rendu") + "</span>"
                    + "<span class=\"chip fold\">" + Ic(IcFolder) + "Mission Atlas</span></div>";

        string act = "<div class=\"m-act\"><span class=\"b dark\">" + Ti(IcGen) + (en ? "Generate minutes" : "Générer un compte rendu") + "</span>"
                   + "<span class=\"b\">" + Ti(IcRefresh) + (en ? "Re-run transcription" : "Relancer la transcription") + "</span>"
                   + "<span class=\"sp\"></span><span class=\"b\">" + Ti(IcShare) + (en ? "Share &amp; export" : "Partager &amp; exporter") + "</span></div>";

        string T(string id, string label, string ic, string ct)
        {
            string c = string.IsNullOrEmpty(ct) ? "" : "<span class=\"ct\">" + ct + "</span>";
            return "<span class=\"t" + (tab == id ? " on" : "") + "\">" + Ic(ic) + label + c + "</span>";
        }

        string tabs = "<div class=\"m-tabs\">"
            + T("summary", en ? "Summary" : "Résumé", IcBookmark, "")
            + T("transcript", en ? "Transcript" : "Transcription", IcGen, "6")
            + T("cr", en ? "Minutes" : "Compte rendu", IcDoc, "2")
            + T("screens", en ? "Screens" : "Captures", IcImg, "3")
            + T("participants", en ? "Attendees" : "Participants", IcUsers, "6")
            + T("notes", en ? "Notes &amp; key moments" : "Notes &amp; moments clés", IcNote, "")
            + "</div>";

        return "<div class=\"m-top\"><span class=\"aicn\" style=\"width:20px;height:20px;color:var(--faint)\">" + Ti(IcChevL) + "</span>"
             + "<span class=\"m-title\">" + MTitle(en) + "</span>"
             + "<span class=\"aicn\" style=\"color:var(--faint)\">" + Ti(IcPencil) + "</span></div>"
             + meta + act + tabs;
    }

    private static string MockHome(bool en)
    {
        string bannerName = MTitle(en);
        string ban = "<div class=\"a-ban\"><span class=\"rd\"></span><span class=\"tmr\">49:33</span>"
                   + "<span class=\"bn\">" + bannerName + "</span><span class=\"sp\"></span>"
                   + "<span class=\"bt g\">Widget</span>"
                   + "<span class=\"bt r\">" + (en ? "Stop &amp; generate minutes" : "Arrêter &amp; générer le CR") + "</span></div>";

        string Stat(string k, string v) => "<div class=\"a-stat\"><div class=\"k\">" + k + "</div><div class=\"v\">" + v + "</div></div>";
        string stats = "<div class=\"a-stats\">"
            + Stat(en ? "Meetings" : "Réunions", "5")
            + Stat(en ? "Minutes" : "Comptes rendus", "2")
            + Stat(en ? "Folders" : "Dossiers", "2") + "</div>";

        string Item(string title, string sub, string chip) =>
            "<div class=\"a-item\"><span class=\"mi\">" + Ti(IcNote) + "</span>"
          + "<div><div class=\"mt\">" + title + "</div><div class=\"md\">" + sub + "</div></div>"
          + "<span class=\"sp\"></span><span class=\"chip ok\">" + chip + "</span></div>";

        string list = "<div class=\"a-sub\"><b>" + (en ? "Recent meetings" : "Réunions récentes") + "</b>"
            + "<span class=\"lk\">" + (en ? "See all" : "Tout voir") + "</span></div>"
            + Item(MTitle(en), (en ? "29 Jun · 3:34 PM · 1:22:50" : "29 juin · 15h34 · 1:22:50") + " · Mission Atlas", en ? "Minutes" : "Compte rendu")
            + Item(en ? "Steering committee #3" : "Comité de pilotage #3", (en ? "26 Jun · 9:31 AM · 35:12" : "26 juin · 09h31 · 35:12") + " · Mission Atlas", en ? "Minutes" : "Compte rendu");

        string main = "<h2 class=\"a-h1\">" + (en ? "Hello Julien 👋" : "Bonjour Julien 👋") + "</h2>"
            + "<p class=\"a-lead\">" + (en ? "Your latest meetings and mission folders." : "Vos dernières réunions et vos dossiers de mission.") + "</p>"
            + ban + stats + list;

        return Frame(en, en ? "Home" : "Accueil", true, "home", main);
    }

    private static string MockTranscript(bool en)
    {
        string bar = "<div class=\"trbar\"><b style=\"color:var(--ink)\">Voxtral</b> "
            + (en ? "· 6 turns · 4 speakers" : "· 6 prises de parole · 4 locuteurs")
            + "<span class=\"sp\"></span><span class=\"se\" style=\"min-width:160px\">" + Ic(IcSearch) + (en ? "Search the transcript" : "Rechercher dans le transcript") + "</span>"
            + "<span class=\"b2 ghost\" style=\"padding:6px 11px\">" + Ti(IcCopy) + "&nbsp;" + (en ? "Copy" : "Copier") + "</span></div>";

        string Row(string time, string ini, string col, string who, string txt, bool unk = false) =>
            "<div class=\"tr" + (unk ? " unk" : "") + "\"><span class=\"tm\">" + time + "</span>"
          + "<span class=\"who\"><span class=\"av\" style=\"background:" + col + "\">" + ini + "</span>" + who
          + (unk ? "<span class=\"aicn\" style=\"width:13px;height:13px;color:var(--faint)\">" + Ti(IcPencil) + "</span>" : "")
          + "</span><span class=\"txt\">" + txt + "</span></div>";

        string rows;
        if (en)
        {
            rows = Row("00:02", "JP", "#2f6db0", "Julien Petit", "Thanks everyone for joining this kick-off. Today the goal is to lock the scope and share the six-month plan.")
                 + Row("00:42", "CM", "#12a4a4", "Claire Moreau", "Great. On my side, the top priority is the backend migration before end of February. We've already flagged the critical batches.")
                 + Row("01:15", "TP", "#e8730a", "Thomas Petit", "One thing: there's a real dependency on the design team. If resources slip, we lose time on batch 2.")
                 + Row("02:03", "1", "", "Speaker 1", "On the budget, we're confirmed at 240 K€. I'd suggest a checkpoint on 15 January.", true)
                 + Row("02:48", "JP", "#2f6db0", "Julien Petit", "Sounds good. Let's set a weekly committee on Tuesdays and a monthly steering meeting.");
        }
        else
        {
            rows = Row("00:02", "JP", "#2f6db0", "Julien Petit", "Merci à tous d'être présents pour ce lancement. Aujourd'hui, l'objectif est de valider le périmètre et de partager le planning sur six mois.")
                 + Row("00:42", "CM", "#12a4a4", "Claire Moreau", "Parfait. De mon côté, la priorité reste la migration du backend avant fin février. Nous avons déjà identifié les lots critiques.")
                 + Row("01:15", "TP", "#e8730a", "Thomas Petit", "Un point d'attention : il y a une vraie dépendance à l'équipe de conception. Si les ressources glissent, on prend du retard sur le lot 2.")
                 + Row("02:03", "1", "", "Locuteur 1", "Sur le budget, nous sommes confirmés à 240 K€. Je propose un point de contrôle au 15 janvier.", true)
                 + Row("02:48", "JP", "#2f6db0", "Julien Petit", "Très bien. On acte un comité hebdomadaire le mardi et un COPIL mensuel.");
        }

        return Frame(en, Crumb(en, MTitle(en)), false, "", MeetHeader(en, "transcript") + bar + rows);
    }

    private static string MockCr(bool en)
    {
        string Tpl(string ic, string name, string desc, bool on, bool wide = false) =>
            "<div class=\"crtpl" + (on ? " on" : "") + "\"" + (wide ? " style=\"grid-column:1/-1\"" : "") + ">"
          + "<span class=\"ic\">" + Ti(ic) + "</span><div><div class=\"nm\">" + name + "</div>"
          + "<div class=\"ds\">" + desc + "</div></div><span class=\"rd\"></span></div>";

        string grid = "<div class=\"crgrid\">"
            + Tpl(IcDoc, en ? "Detailed" : "Détaillé", en ? "Structured, 4 sections" : "Structure détaillée, 4 sections", true)
            + Tpl(IcBookmark, en ? "Summary" : "Synthèse", en ? "Short recap" : "Synthèse courte", false)
            + Tpl(IcDoc, en ? "Detailed client minutes" : "CR client détaillé", en ? "Structured · custom guidance" : "Structure détaillée · consignes personnalisées", false, true)
            + "</div>";

        string gen = "<div class=\"crgen\"><span class=\"se\" style=\"min-width:150px\">" + Ic(IcGlobe) + (en ? "Same as the meeting" : "Même langue que la réunion") + "</span>"
            + "<span class=\"b2 dark\">" + (en ? "Generate minutes" : "Générer le compte rendu") + "</span></div>";

        string file = "<div class=\"crfile\"><div class=\"hd\">" + Ic(IcDoc) + "Atlas - Minutes.md<span class=\"sp\"></span><span class=\"chip ok\">" + (en ? "Ready" : "Prêt") + "</span></div>"
            + "<h4>" + MTitle(en) + "</h4>"
            + "<p style=\"color:var(--ink);font-weight:600\">" + (en ? "1. Context" : "1. Contexte") + "</p>"
            + "<p>" + (en
                ? "Kick-off meeting held on 29 June 2026 (52 min, 5 attendees). Goal: lock the scope and share the six-month plan."
                : "Réunion de lancement tenue le 29 juin 2026 (52 min, 5 participants). Objectif : valider le périmètre et partager le planning sur six mois.") + "</p></div>";

        string head = "<div class=\"a-sub\" style=\"margin-top:12px\"><b>" + (en ? "Minutes template" : "Modèle de compte rendu") + "</b>"
            + "<span class=\"lk\">" + (en ? "Manage templates" : "Gérer les modèles") + "</span></div>";

        return Frame(en, Crumb(en, MTitle(en)), false, "", MeetHeader(en, "cr") + head + grid + gen + file);
    }

    private static string MockParticipants(bool en)
    {
        string P(string ini, string col, string name, string email, bool has) =>
            "<div class=\"pt\"><span class=\"av\" style=\"background:" + col + "\">" + ini + "</span>"
          + "<div><div class=\"nm\">" + name + "</div><div class=\"em" + (has ? "" : " no") + "\">" + email + "</div></div>"
          + "<span class=\"sp\"></span><span class=\"b\">" + (has ? (en ? "Edit e-mail" : "Modifier e-mail") : (en ? "Add e-mail" : "Ajouter e-mail")) + "</span></div>";

        string noMail = en ? "no e-mail yet" : "e-mail non renseigné";
        string rows = P("CM", "#12a4a4", "Claire Moreau", "claire.moreau@exemple.com", true)
                    + P("TP", "#e8730a", "Thomas Petit", noMail, false)
                    + P("JP", "#2f6db0", "Julien Petit", noMail, false)
                    + P("JF", "#17aed0", "Julie Fontaine", noMail, false);

        return Frame(en, Crumb(en, MTitle(en)), false, "", MeetHeader(en, "participants") + "<div style=\"margin-top:6px\">" + rows + "</div>");
    }

    private static string MockShare(bool en)
    {
        string bullets = "<ul class=\"sm\">"
            + "<li>" + (en ? "Project officially launched: scope validated, six-month plan shared." : "Lancement officiel du projet : périmètre validé, planning partagé sur six mois.") + "</li>"
            + "<li><b>" + (en ? "Budget confirmed at 240 K€" : "Budget confirmé à 240 K€") + "</b>" + (en ? ", checkpoint set for 15 January." : ", point de contrôle fixé au 15 janvier.") + "</li>"
            + "<li>" + (en ? "Governance agreed: weekly committee on Tuesdays plus a monthly steering meeting." : "Gouvernance actée : comité hebdomadaire le mardi et COPIL mensuel.") + "</li>"
            + "<li>" + (en ? "Main risk: dependency on the design team (batch 2)." : "Risque principal : dépendance à l'équipe de conception (lot 2).") + "</li></ul>";

        string sum = "<div class=\"a-sub\" style=\"margin-top:10px\"><b>" + (en ? "Meeting summary" : "Résumé de la réunion") + "</b></div>" + bullets;

        string MI(string ic, string t) => "<div class=\"mi\">" + Ic(ic) + t + "</div>";
        string menu = "<div class=\"shmenu\">"
            + "<div class=\"g\">" + (en ? "OPEN" : "OUVRIR") + "</div>"
            + MI(IcFolder, en ? "Meeting folder" : "Dossier de la réunion")
            + MI(IcDoc, "Atlas - Minutes.eml")
            + MI(IcDoc, "Atlas - Minutes.md")
            + "<div class=\"g\">" + (en ? "AUDIO" : "AUDIO") + "</div>"
            + MI(IcRec(), "mic.mp3")
            + MI(IcRec(), "system.mp3")
            + "<div class=\"g\">" + (en ? "FOR CLAUDE" : "POUR CLAUDE") + "</div>"
            + MI(IcDoc, en ? "Create the Claude folder (.md)" : "Créer le dossier Claude (.md)")
            + "</div>";

        return Frame(en, Crumb(en, MTitle(en)), false, "", MeetHeader(en, "summary") + sum + menu, rel: true);
    }

    private static string IcRec() => "<path d=\"M12 3a3 3 0 0 1 3 3v5a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z\"/><path d=\"M6 11a6 6 0 0 0 12 0M12 17v3\"/>";

    private static string MockApi(bool en)
    {
        string N(string ic, string label, bool on) => "<div class=\"it" + (on ? " on" : "") + "\">" + Ic(ic) + label + "</div>";
        string nav = "<div class=\"stnav\">"
            + N(IcUsers, en ? "Account" : "Compte", false)
            + N(IcRec(), en ? "Recording" : "Enregistrement", false)
            + N(IcGen, "Transcription", false)
            + N(IcDoc, en ? "Minutes" : "Comptes rendus", false)
            + N(IcLink, en ? "Integrations" : "Intégrations", true)
            + N(IStart, en ? "General" : "Général", false)
            + "</div>";

        string main = "<div class=\"stmain\"><h4 style=\"font-family:var(--disp);color:var(--ink);margin:0 0 4px;font-size:14px\">"
            + (en ? "Integrations &amp; API key" : "Intégrations &amp; clé API") + "</h4>"
            + "<div class=\"strow\"><div class=\"lb\"><div class=\"t\">" + (en ? "Mistral API key" : "Clé API Mistral") + "</div>"
              + "<div class=\"s\" style=\"color:var(--ok)\">• " + (en ? "Set ✓" : "Définie ✓") + "</div></div>"
              + "<span class=\"stin\">" + (en ? "Paste the key…" : "Coller la clé…") + "</span><span class=\"stb dark\">" + (en ? "Save" : "Enregistrer") + "</span></div>"
            + "<div class=\"strow\"><div class=\"lb\"><div class=\"t\">" + (en ? "Test the key" : "Tester la clé") + "</div>"
              + "<div class=\"s\">" + (en ? "Checks the connection to Mistral" : "Vérifie la connexion à Mistral") + "</div></div>"
              + "<span class=\"stb gh\">" + (en ? "Test" : "Tester") + "</span></div>"
            + "<div class=\"strow\"><span class=\"tsq\">T</span><div class=\"lb\"><div class=\"t\">Microsoft Teams</div>"
              + "<div class=\"s\">" + (en ? "Auto meeting detection + speaker names" : "Détection auto des réunions + lecture des participants") + "</div></div>"
              + "<span class=\"stb gh\">" + (en ? "Import a transcript" : "Importer un transcript") + "</span></div>"
            + "</div>";

        return "<div class=\"app\"><div class=\"stwrap\">" + nav + main + "</div></div>";
    }

    private static string MockWidget(bool en, bool notes)
    {
        string head = "<div class=\"h\"><span class=\"ic\">" + Ti(IRec) + "</span>"
            + "<span class=\"ti\"><span class=\"nm\">" + MTitle(en) + "</span>"
            + "<span class=\"st\">" + (en ? "Recording in progress" : "Enregistrement en cours") + "</span></span>"
            + "<span class=\"hb\">" + Ti(IcPin) + Ti(IcChevL) + Ti(IcMin) + "</span></div>";

        string cap = "<div class=\"cap\">" + Ic(IcImg) + "48:28 &nbsp;" + (en ? "Last capture" : "Dernière capture")
            + "<span class=\"sp\"></span><span class=\"ct\">3</span></div>";

        string slide = "<div class=\"slide\"><i style=\"left:16px;top:20px;width:120px\"></i>"
            + "<i style=\"left:16px;top:36px;width:80px\"></i>"
            + "<i style=\"right:20px;top:20px;width:64px;height:40px;border-radius:8px;background:rgba(255,255,255,.35)\"></i></div>";

        string mid;
        if (notes)
        {
            mid = "<div class=\"nsec\"><div class=\"hd\">NOTES <span class=\"fn\">notes.md</span><span class=\"sp\" style=\"flex:1\"></span>0</div>"
                + "<div class=\"empty\">" + (en
                    ? "No note yet.<br>Every note is time-stamped and saved to <b>notes.md</b>."
                    : "Aucune note.<br>Chaque note est horodatée et enregistrée dans <b>notes.md</b>.") + "</div></div>"
                + "<div class=\"ninput\"><span class=\"sp\">" + (en ? "Take a note…" : "Prendre une note…") + "</span>"
                + "<span class=\"b\">" + Ti(IcLink) + "</span><span class=\"b\">" + Ti(IcClock) + "</span><span class=\"b dark\">" + Ti(IcArrowR) + "</span></div>";
        }
        else
        {
            mid = "<div class=\"noteln\">" + Ic(IcPencil) + (en ? "Click to type a note, or Enter…" : "Cliquez pour saisir une note, ou Entrée…") + "</div>";
        }

        string ft = "<div class=\"ft\"><span class=\"aicn\" style=\"color:var(--faint)\">" + Ti(IRec) + "</span><span class=\"tm\">49:33</span><span class=\"sp\"></span>"
            + "<span class=\"b" + (notes ? " on" : "") + "\">" + Ti(IcNote) + "</span>"
            + "<span class=\"b\">" + Ti(IcFlag) + "</span>"
            + "<span class=\"b\">" + Ti(IcImg) + "</span>"
            + "<span class=\"b stop\"><i></i></span></div>";

        return "<div class=\"wg\">" + head + cap + slide + mid + ft + "</div>";
    }

    // --- maquettes console Mistral (non capturable : connexion requise) -------
    private static string MkMistral(string lang)
    {
        var (t1, t2) = lang == "en" ? ("Create", "Create an API key") : ("Créer", "Créer une clé API");
        return MkMistralTpl.Replace("__T1__", t1).Replace("__T2__", t2);
    }

    private const string MkMistralTpl = """
<div class="shot mistral"><div class="urlbar"><span class="d"><i></i><i></i><i></i></span><span class="u">console.mistral.ai</span></div><div class="in"><div class="mcard"><div class="t">__T1__</div><div class="mrow">Playground <span class="ar">&rarr;</span></div><div class="mrow">Workflows <span class="ar">&rarr;</span></div><div class="mrow hot">__T2__ <span class="ar">&rarr;</span></div></div></div></div>
""";

    private static string MkDialog(string lang)
    {
        var (t0, t1, t2, t3) = lang == "en"
            ? ("Create an API key", "Key name", "Cancel", "Create new key")
            : ("Créer une clé API", "Nom de la clé", "Annuler", "Nouvelle clé");
        return MkDialogTpl.Replace("__T0__", t0).Replace("__T1__", t1).Replace("__T2__", t2).Replace("__T3__", t3);
    }

    private const string MkDialogTpl = """
<div class="shot"><div class="dlg"><div class="box"><div class="t">__T0__</div><label>__T1__</label><div class="inp">Echo</div><div class="foot"><span class="b2 ghost">__T2__</span><span class="b2 dark">__T3__</span></div></div></div></div>
""";

    private static string MkSuccess(string lang)
    {
        var (t0, t1, t2) = lang == "en"
            ? ("API key created successfully", "Copy", "Store this key somewhere safe. You will not be able to see it again.")
            : ("Clé API créée avec succès", "Copier", "Enregistrez cette clé dans un endroit sûr. Vous ne pourrez plus la voir.");
        return MkSuccessTpl.Replace("__CHK__", CheckSvg).Replace("__T0__", t0).Replace("__T1__", t1).Replace("__T2__", t2);
    }

    private const string MkSuccessTpl = """
<div class="shot"><div class="okbox"><div class="in"><div class="oh">__CHK__ __T0__</div><div class="keyrow"><code>xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx</code><span class="cp">__T1__</span></div><div class="sub">__T2__</div></div></div></div>
""";

    private const string Js = """
(function(){
  var tabs=[].slice.call(document.querySelectorAll('.tabnav button'));
  var panels=[].slice.call(document.querySelectorAll('.tabpanel'));
  function go(id){
    tabs.forEach(function(t){t.classList.toggle('active',t.getAttribute('data-tab')===id);});
    panels.forEach(function(p){p.classList.toggle('active',p.id===id);});
    try{history.replaceState(null,'','#'+id);}catch(e){}
    window.scrollTo(0,0);
  }
  tabs.forEach(function(t){t.addEventListener('click',function(){go(t.getAttribute('data-tab'));});});
  var h=(location.hash||'').replace('#','');
  go(document.getElementById(h)&&h?h:tabs[0].getAttribute('data-tab'));
})();
""";

    private const string Css = """
:root{
  --navy:#0d2f4b; --navy2:#123f61; --accent:#1e5aa8; --accent-soft:#eaf1f9;
  --ink:#0b1526; --muted:#52616f; --faint:#8a98a6;
  --bg:#f6f8fb; --card:#ffffff; --sunken:#eef2f7; --line:#e6ebf2; --line2:#d7dee8;
  --ok:#1f9d6b; --ok-soft:#e6f5ef; --warn:#d98410; --warn-soft:#fdf1de;
  --rec:#e5484d; --rec-soft:#fdecec;
  --radius:14px; --sh:0 1px 2px rgba(11,21,38,.06);
  --sh2:0 16px 44px -20px rgba(11,21,38,.42);
  --font:"Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,sans-serif;
  --disp:"Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;
  --mono:"Cascadia Code",Consolas,ui-monospace,monospace;
}
@media (prefers-color-scheme:dark){:root{
  --ink:#eaf1f9; --muted:#9fb0c2; --faint:#6a7c8f;
  --bg:#0b1420; --card:#0e1826; --sunken:#111d2c; --line:#1d2b3c; --line2:#243349;
  --navy:#2f6db0; --navy2:#3f7cbf; --accent:#5aa0f0; --accent-soft:#16283d;
  --ok-soft:#10261d; --warn-soft:#2a1f0c; --rec-soft:#2c1416;
}}
*{box-sizing:border-box}
html{scroll-behavior:smooth}
body{margin:0;background:var(--bg);color:var(--ink);font-family:var(--font);
  font-size:15px;line-height:1.65;-webkit-font-smoothing:antialiased}
a{color:var(--accent)}
.top{position:sticky;top:0;z-index:30;background:color-mix(in srgb,var(--bg) 84%,transparent);
  backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);border-bottom:1px solid var(--line)}
.top .row{max-width:900px;margin:0 auto;display:flex;align-items:center;gap:12px;padding:11px 22px}
.logo{width:32px;height:32px;border-radius:9px;flex:none;display:grid;place-items:center;
  background:linear-gradient(150deg,var(--navy),var(--accent));
  box-shadow:inset 0 1.5px 1px rgba(255,255,255,.6),0 4px 12px -4px rgba(13,42,89,.5)}
.logo svg{width:18px;height:18px;color:#fff}
.bname{font-family:var(--disp);font-weight:700;font-size:18px;letter-spacing:.2px}
.bname small{font-weight:500;font-size:11px;color:var(--faint);margin-left:6px}
.tabnav{max-width:900px;margin:0 auto;display:flex;gap:4px;padding:8px 16px 0;overflow-x:auto}
.tabnav button{display:inline-flex;align-items:center;gap:8px;white-space:nowrap;border:0;cursor:pointer;
  font:inherit;font-size:14px;font-weight:600;color:var(--muted);background:transparent;
  padding:11px 14px;border-radius:10px 10px 0 0;border-bottom:2px solid transparent;transition:.15s}
.tabnav button svg{width:16px;height:16px}
.tabnav button:hover{color:var(--ink);background:var(--sunken)}
.tabnav button.active{color:var(--accent);border-bottom-color:var(--accent)}
.wrap{max-width:900px;margin:0 auto;padding:26px 22px 72px}
.tabpanel{display:none;animation:fade .28s ease both}
.tabpanel.active{display:block}
@keyframes fade{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}
h1{font-family:var(--disp);font-size:28px;font-weight:700;letter-spacing:-.6px;margin:0 0 10px}
.lead{color:var(--muted);max-width:660px;margin:0 0 8px;font-size:16px;line-height:1.6}
h2{font-family:var(--disp);font-size:19px;font-weight:700;letter-spacing:-.3px;margin:34px 0 4px;
  display:flex;align-items:center;gap:10px}
h2 .ic{width:26px;height:26px;border-radius:8px;flex:none;background:var(--accent-soft);color:var(--accent);
  display:grid;place-items:center}
h2 .ic svg{width:15px;height:15px}
h3{font-family:var(--disp);font-size:15.5px;font-weight:700;margin:20px 0 6px;color:var(--ink)}
p,li{color:var(--muted)}
b,strong{color:var(--ink);font-weight:600}
ul,ol{padding-left:20px;margin:8px 0}
li{margin:7px 0}
.card{background:var(--card);border:1px solid var(--line);border-radius:var(--radius);
  padding:6px 24px;margin-top:16px;box-shadow:var(--sh)}
.card.pad{padding:20px 24px}
.steps{list-style:none;margin:4px 0;padding:0;counter-reset:s}
.step{display:grid;grid-template-columns:30px 1fr;gap:15px;padding:18px 0;border-top:1px solid var(--line)}
.step:first-child{border-top:0}
.step .n{counter-increment:s;width:28px;height:28px;border-radius:50%;flex:none;
  background:var(--accent-soft);color:var(--accent);font-weight:700;font-size:14px;display:grid;place-items:center}
.step .n::before{content:counter(s)}
.step .bd{min-width:0}
.step .bd h3{margin:1px 0 4px;font-size:15.5px;color:var(--ink)}
.step .bd p{margin:0}
.btn{display:inline-flex;align-items:center;gap:9px;padding:11px 18px;border-radius:11px;
  background:linear-gradient(160deg,var(--navy),var(--navy2));color:#fff;text-decoration:none;
  font-weight:600;font-size:14.5px;margin-top:14px;
  box-shadow:inset 0 1.5px 1px rgba(255,255,255,.35),0 10px 22px -12px rgba(13,42,89,.85)}
.btn svg{width:16px;height:16px}
.btn:hover{filter:brightness(1.07)}
.mono{font-family:var(--mono);font-size:13px;background:var(--sunken);border:1px solid var(--line);
  border-radius:6px;padding:1.5px 7px;color:var(--ink)}
kbd{font-family:var(--mono);font-size:12.5px;background:var(--navy);color:#fff;border-radius:6px;
  padding:2px 7px;border:1px solid var(--navy)}
.note,.warn,.tip{border-radius:11px;padding:12px 16px;margin:16px 0;font-size:14px;
  display:flex;gap:11px;align-items:flex-start;line-height:1.55}
.note{background:var(--accent-soft);border:1px solid color-mix(in srgb,var(--accent) 22%,transparent);color:var(--ink)}
.tip{background:var(--ok-soft);border:1px solid color-mix(in srgb,var(--ok) 30%,transparent);color:var(--ink)}
.warn{background:var(--warn-soft);border:1px solid color-mix(in srgb,var(--warn) 34%,transparent);color:var(--ink)}
.note svg,.tip svg,.warn svg{width:18px;height:18px;flex:none;margin-top:1px}
.note svg{color:var(--accent)}.tip svg{color:var(--ok)}.warn svg{color:var(--warn)}
table{border-collapse:collapse;width:100%;margin-top:10px;font-size:14px;
  border:1px solid var(--line);border-radius:12px;overflow:hidden}
th,td{padding:10px 12px;text-align:left;vertical-align:top;border-bottom:1px solid var(--line)}
th{background:var(--navy);color:#fff;font-weight:600}
td{color:var(--muted)}td b{color:var(--ink)}
tr:last-child td{border-bottom:0}
tbody tr:nth-child(even) td{background:color-mix(in srgb,var(--sunken) 55%,transparent)}
.wic{display:inline-grid;place-items:center;width:27px;height:27px;border-radius:8px;
  background:var(--sunken);border:1px solid var(--line2);vertical-align:middle}
.wic svg{width:14px;height:14px;color:var(--muted)}
.wic.acc{background:var(--accent-soft);border-color:color-mix(in srgb,var(--accent) 25%,transparent)}
.wic.acc svg{color:var(--accent)}
.wic.rec{background:var(--rec-soft);border-color:color-mix(in srgb,var(--rec) 30%,transparent)}
.wic.rec svg{color:var(--rec)}
.figcap{font-size:12.5px;color:var(--faint);margin:8px 2px 0;text-align:center}
.figrow{display:flex;gap:16px;flex-wrap:wrap;align-items:flex-start}
.figrow>div{flex:1;min-width:230px}

/* --- maquettes de l'application (anonymes, thème clair/sombre) ------------ */
.app{margin:16px 0 4px;border:1px solid var(--line2);border-radius:14px;overflow:hidden;
  box-shadow:var(--sh2);background:var(--card);font-size:12.5px;line-height:1.45}
.app *{box-sizing:border-box}
.abar{display:flex;align-items:center;gap:9px;padding:8px 13px;border-bottom:1px solid var(--line)}
.abar .lg{width:21px;height:21px;border-radius:6px;flex:none;display:grid;place-items:center;
  background:linear-gradient(150deg,var(--navy),var(--accent))}
.abar .lg svg{width:12px;height:12px;color:#fff}
.abar .nm{font-family:var(--disp);font-weight:700;font-size:12.5px;color:var(--ink)}
.abar .cr{color:var(--faint);margin-left:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.abar .cr b{color:var(--muted);font-weight:500}
.abar .sp{flex:1}
.abar .se{display:flex;align-items:center;gap:6px;color:var(--faint);background:var(--sunken);
  border:1px solid var(--line);border-radius:8px;padding:4px 10px;font-size:11.5px}
.abar .wb{display:flex;gap:7px;color:var(--faint)}
.abar .wb svg{width:13px;height:13px}
.abody{display:flex;min-height:290px}
.aside{width:186px;flex:none;padding:12px 11px;border-right:1px solid var(--line);
  background:color-mix(in srgb,var(--sunken) 45%,transparent);display:flex;flex-direction:column;gap:6px}
.amain{flex:1;min-width:0;padding:16px 18px;color:var(--muted)}
.amain.rel{position:relative}
.aicn{display:inline-grid;place-items:center;width:15px;height:15px;flex:none}
.aicn svg{width:100%;height:100%}
.pbtn{display:flex;align-items:center;justify-content:center;gap:8px;padding:9px;border-radius:9px;
  font-weight:600;color:#fff;background:linear-gradient(160deg,var(--navy),var(--navy2));font-size:12px}
.pbtn.rec{background:linear-gradient(160deg,#e5484d,#c93b40)}
.pbtn .dot{width:7px;height:7px;border-radius:50%;background:#fff;flex:none}
.srow{display:flex;gap:6px}
.sgh{flex:1;display:flex;align-items:center;justify-content:center;gap:6px;padding:7px;border-radius:8px;
  border:1px solid var(--line2);background:var(--card);color:var(--muted);font-size:11.5px}
.sgh .aicn{width:13px;height:13px}
.anav{display:flex;flex-direction:column;gap:2px;margin-top:4px}
.anav .it{display:flex;align-items:center;gap:9px;padding:7px 9px;border-radius:8px;color:var(--muted)}
.anav .it .ct{margin-left:auto;background:var(--sunken);border-radius:20px;padding:0 7px;font-size:10.5px;color:var(--faint)}
.anav .it.on{background:var(--accent-soft);color:var(--accent);font-weight:600}
.asec{display:flex;align-items:center;justify-content:space-between;font-size:10px;font-weight:700;
  letter-spacing:.08em;color:var(--faint);margin:10px 4px 3px}
.asec span{display:inline-grid;place-items:center;width:14px;height:14px}
.asec span svg{width:100%;height:100%}
.afold{display:flex;align-items:center;gap:9px;padding:6px 9px;border-radius:8px;color:var(--muted)}
.afold .fd{width:8px;height:8px;border-radius:50%;flex:none}
.a-h1{font-family:var(--disp);font-weight:700;font-size:19px;color:var(--ink);margin:0}
.a-lead{margin:3px 0 14px;color:var(--muted)}
.a-ban{display:flex;align-items:center;gap:11px;padding:11px 14px;border-radius:11px;
  background:linear-gradient(120deg,var(--navy),var(--navy2))}
.a-ban .tmr{font-family:var(--mono);font-weight:700;color:#fff;font-size:14px}
.a-ban .rd{width:8px;height:8px;border-radius:50%;background:#ff6b6b;flex:none}
.a-ban .bn{color:#fff;font-weight:600}
.a-ban .sp{flex:1}
.a-ban .bt{padding:6px 11px;border-radius:8px;font-weight:600;font-size:11.5px;white-space:nowrap}
.a-ban .bt.g{background:rgba(255,255,255,.15);color:#fff}
.a-ban .bt.r{background:#e5484d;color:#fff}
.a-stats{display:grid;grid-template-columns:repeat(3,1fr);gap:11px;margin:14px 0}
.a-stat{border:1px solid var(--line);border-radius:11px;padding:11px 13px;background:var(--card)}
.a-stat .k{color:var(--muted);font-size:11.5px}
.a-stat .v{font-family:var(--disp);font-weight:700;font-size:20px;color:var(--ink);margin-top:3px}
.a-sub{display:flex;align-items:center;justify-content:space-between;margin:6px 0}
.a-sub b{color:var(--ink);font-family:var(--disp)}
.a-sub .lk{color:var(--accent);font-size:11.5px}
.a-item{display:flex;align-items:center;gap:11px;border:1px solid var(--line);border-radius:11px;
  padding:10px 13px;margin-top:8px;background:var(--card)}
.a-item .mi{width:26px;height:26px;border-radius:8px;flex:none;display:grid;place-items:center;
  background:var(--sunken);color:var(--muted)}
.a-item .mi svg{width:14px;height:14px}
.a-item .mt{color:var(--ink);font-weight:600}
.a-item .md{color:var(--faint);font-size:11px;margin-top:2px}
.a-item .sp{flex:1}
.chip{display:inline-flex;align-items:center;gap:5px;font-size:11px;padding:2px 9px;border-radius:20px;white-space:nowrap}
.chip .aicn{width:12px;height:12px}
.chip.ok{background:var(--ok-soft);color:var(--ok)}
.chip.fold{background:var(--sunken);color:var(--muted)}
.m-top{display:flex;align-items:center;gap:9px}
.m-title{font-family:var(--disp);font-weight:700;font-size:18px;color:var(--ink)}
.m-meta{display:flex;align-items:center;gap:9px;color:var(--faint);font-size:11.5px;margin:9px 0 2px;flex-wrap:wrap}
.m-ava{display:flex}
.m-ava span{width:19px;height:19px;border-radius:50%;margin-left:-5px;border:2px solid var(--card);
  display:grid;place-items:center;font-size:8.5px;font-weight:700;color:#fff}
.m-act{display:flex;gap:9px;margin:13px 0;flex-wrap:wrap}
.m-act .b{padding:8px 12px;border-radius:9px;font-weight:600;font-size:11.5px;border:1px solid var(--line2);
  color:var(--muted);background:var(--card);display:flex;align-items:center;gap:7px}
.m-act .b svg{width:13px;height:13px}
.m-act .b.dark{background:linear-gradient(160deg,var(--navy),var(--navy2));color:#fff;border:0}
.m-act .sp{flex:1}
.m-tabs{display:flex;gap:15px;border-bottom:1px solid var(--line);margin-top:6px;overflow-x:auto}
.m-tabs .t{display:flex;align-items:center;gap:6px;padding:9px 1px;color:var(--muted);white-space:nowrap;
  border-bottom:2px solid transparent}
.m-tabs .t .aicn{width:13px;height:13px}
.m-tabs .t .ct{background:var(--sunken);border-radius:20px;padding:0 6px;font-size:9.5px}
.m-tabs .t.on{color:var(--accent);border-bottom-color:var(--accent);font-weight:600}
.trbar{display:flex;align-items:center;gap:10px;margin:14px 0 2px;color:var(--muted);font-size:11.5px;flex-wrap:wrap}
.trbar .sp{flex:1}
.trbar .se{display:flex;align-items:center;gap:6px;color:var(--faint);background:var(--sunken);
  border:1px solid var(--line);border-radius:8px;padding:4px 10px}
.tr{display:grid;grid-template-columns:42px 148px 1fr;gap:10px;padding:11px 0;border-top:1px solid var(--line);align-items:start}
.tr .tm{font-family:var(--mono);color:var(--faint);font-size:11px}
.tr .who{display:flex;align-items:center;gap:7px;color:var(--ink);font-weight:600}
.tr .who .av{width:19px;height:19px;border-radius:50%;display:grid;place-items:center;font-size:8.5px;font-weight:700;color:#fff;flex:none}
.tr .who .aicn{width:12px;height:12px;color:var(--faint)}
.tr .txt{color:var(--muted)}
.tr.unk .who{color:var(--faint);font-weight:500;font-style:italic}
.tr.unk .av{background:var(--sunken)!important;color:var(--faint)}
.crgrid{display:grid;grid-template-columns:1fr 1fr;gap:11px;margin:12px 0}
.crtpl{border:1px solid var(--line2);border-radius:11px;padding:12px 14px;display:flex;gap:10px;
  align-items:flex-start;background:var(--card)}
.crtpl.on{border-color:var(--accent);background:var(--accent-soft)}
.crtpl .ic{width:26px;height:26px;border-radius:7px;flex:none;display:grid;place-items:center;
  background:var(--card);border:1px solid var(--line);color:var(--accent)}
.crtpl .ic svg{width:14px;height:14px}
.crtpl .nm{color:var(--ink);font-weight:600}
.crtpl .ds{color:var(--faint);font-size:11px;margin-top:2px}
.crtpl .rd{margin-left:auto;width:16px;height:16px;border-radius:50%;border:2px solid var(--line2);flex:none}
.crtpl.on .rd{border-color:var(--accent);background:var(--accent);box-shadow:inset 0 0 0 3px var(--card)}
.crgen{display:flex;align-items:center;gap:10px;margin:4px 0 12px;flex-wrap:wrap}
.crgen .se{display:flex;align-items:center;gap:6px;color:var(--muted);background:var(--card);
  border:1px solid var(--line2);border-radius:8px;padding:7px 11px}
.crfile{border:1px solid var(--line);border-radius:11px;padding:12px 14px;background:var(--sunken)}
.crfile .hd{display:flex;align-items:center;gap:8px;color:var(--muted);font-size:11.5px}
.crfile .hd .sp{flex:1}
.crfile h4{font-family:var(--disp);color:var(--ink);margin:10px 0 6px;font-size:13px}
.crfile p{margin:4px 0;color:var(--muted)}
.pt{display:flex;align-items:center;gap:11px;border:1px solid var(--line);border-radius:11px;
  padding:11px 13px;margin-top:8px;background:var(--card)}
.pt .av{width:26px;height:26px;border-radius:50%;display:grid;place-items:center;font-size:10px;font-weight:700;color:#fff;flex:none}
.pt .nm{color:var(--ink);font-weight:600}
.pt .em{color:var(--faint);font-size:11px;margin-top:1px}
.pt .em.no{font-style:italic}
.pt .sp{flex:1}
.pt .b{padding:6px 11px;border-radius:8px;border:1px solid var(--line2);color:var(--muted);font-size:11px;white-space:nowrap}
.sm{list-style:none;padding:0;margin:8px 0}
.sm li{display:flex;gap:9px;padding:5px 0;color:var(--muted)}
.sm li::before{content:"";width:6px;height:6px;border-radius:50%;background:var(--accent);flex:none;margin-top:7px}
.shmenu{position:absolute;top:112px;right:16px;width:216px;background:var(--card);border:1px solid var(--line2);
  border-radius:11px;box-shadow:var(--sh2);padding:7px;font-size:11.5px;z-index:5}
.shmenu .g{font-size:9.5px;font-weight:700;letter-spacing:.08em;color:var(--faint);padding:8px 8px 3px}
.shmenu .mi{display:flex;align-items:center;gap:8px;padding:7px 8px;border-radius:7px;color:var(--muted)}
.shmenu .mi .aicn{width:13px;height:13px;color:var(--faint)}
.stwrap{display:flex;min-height:210px}
.stnav{width:158px;flex:none;border-right:1px solid var(--line);padding:12px;display:flex;flex-direction:column;gap:3px;
  background:color-mix(in srgb,var(--sunken) 45%,transparent)}
.stnav .it{display:flex;align-items:center;gap:8px;padding:7px 9px;border-radius:8px;color:var(--muted)}
.stnav .it.on{background:var(--accent-soft);color:var(--accent);font-weight:600}
.stmain{flex:1;padding:16px 18px}
.strow{display:flex;align-items:center;gap:12px;padding:12px 0;border-top:1px solid var(--line)}
.strow:first-of-type{border-top:0}
.strow .lb{flex:1}
.strow .lb .t{color:var(--ink);font-weight:600}
.strow .lb .s{color:var(--faint);font-size:11px;margin-top:2px}
.stin{background:var(--card);border:1px solid var(--line2);border-radius:8px;padding:8px 11px;color:var(--faint);min-width:150px}
.stb{padding:8px 14px;border-radius:8px;font-weight:600;font-size:11.5px;white-space:nowrap}
.stb.dark{background:linear-gradient(160deg,var(--navy),var(--navy2));color:#fff}
.stb.gh{border:1px solid var(--line2);color:var(--muted);background:var(--card)}
.tsq{width:26px;height:26px;border-radius:7px;flex:none;display:grid;place-items:center;
  background:#5059c9;color:#fff;font-weight:700;font-size:12px}
.wg{max-width:322px;margin:16px 0 4px;border:1px solid var(--line2);border-radius:14px;
  box-shadow:var(--sh2);background:var(--card);overflow:hidden;font-size:12.5px}
.wg .h{display:flex;align-items:center;gap:8px;padding:11px 12px 3px}
.wg .h .ic{width:16px;height:16px;color:var(--accent);flex:none}
.wg .h .ic svg{width:100%;height:100%}
.wg .h .ti{min-width:0;flex:1}
.wg .h .ti .nm{color:var(--ink);font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:12px}
.wg .h .ti .st{color:#e5484d;font-size:10.5px;display:flex;align-items:center;gap:5px;margin-top:1px}
.wg .h .ti .st::before{content:"";width:6px;height:6px;border-radius:50%;background:#e5484d}
.wg .h .hb{display:flex;gap:6px;color:var(--faint)}
.wg .h .hb svg{width:13px;height:13px}
.wg .cap{display:flex;align-items:center;gap:6px;padding:2px 13px 8px;color:var(--faint);font-size:10.5px}
.wg .cap .aicn{width:13px;height:13px}
.wg .cap .sp{flex:1}
.wg .cap .ct{background:var(--sunken);border-radius:20px;padding:0 7px}
.wg .slide{margin:0 12px;height:74px;border-radius:10px;background:linear-gradient(120deg,var(--navy),var(--accent));position:relative;overflow:hidden}
.wg .slide i{position:absolute;height:7px;border-radius:4px;background:rgba(255,255,255,.5);display:block}
.wg .noteln{margin:11px 12px;border:1px dashed var(--line2);border-radius:9px;padding:9px 11px;
  color:var(--faint);display:flex;align-items:center;gap:7px;font-size:11px}
.wg .noteln .aicn{width:13px;height:13px}
.wg .nsec{margin:11px 12px 0;color:var(--faint)}
.wg .nsec .hd{display:flex;align-items:center;gap:7px;font-size:10px;font-weight:700;letter-spacing:.06em}
.wg .nsec .hd .fn{background:var(--sunken);border-radius:6px;padding:1px 7px;font-family:var(--mono);color:var(--muted);font-weight:500;letter-spacing:0}
.wg .nsec .empty{text-align:center;padding:12px 6px;font-size:11px;color:var(--faint);line-height:1.5}
.wg .ninput{display:flex;align-items:center;gap:7px;margin:10px 12px;border:1px solid var(--accent);border-radius:9px;padding:7px 9px;color:var(--faint);font-size:11px}
.wg .ninput .sp{flex:1}
.wg .ninput .b{width:22px;height:22px;border-radius:7px;display:grid;place-items:center;background:var(--sunken);color:var(--muted)}
.wg .ninput .b svg{width:12px;height:12px}
.wg .ninput .b.dark{background:var(--navy);color:#fff}
.wg .ft{display:flex;align-items:center;gap:9px;padding:9px 12px;border-top:1px solid var(--line);margin-top:11px}
.wg .ft .aicn{width:15px;height:15px}
.wg .ft .tm{font-family:var(--mono);font-weight:700;color:var(--ink)}
.wg .ft .sp{flex:1}
.wg .ft .b{width:27px;height:27px;border-radius:8px;display:grid;place-items:center;background:var(--sunken);border:1px solid var(--line2);color:var(--muted)}
.wg .ft .b svg{width:14px;height:14px}
.wg .ft .b.on{background:var(--accent-soft);border-color:color-mix(in srgb,var(--accent) 25%,transparent);color:var(--accent)}
.wg .ft .b.stop{background:#e5484d;border:0}
.wg .ft .b.stop i{width:9px;height:9px;border-radius:2px;background:#fff}

/* --- maquettes console Mistral ------------------------------------------- */
.shot{margin:14px 0 4px;border-radius:13px;border:1px solid var(--line2);overflow:hidden;
  box-shadow:var(--sh2);background:var(--card)}
.shot .urlbar{display:flex;align-items:center;gap:8px;padding:9px 13px;font-size:12.5px;
  color:#6b7684;background:#f1efe9;border-bottom:1px solid #e4e0d6}
.shot .urlbar .d{display:flex;gap:5px}
.shot .urlbar .d i{width:9px;height:9px;border-radius:50%;background:#d6d1c4;display:block}
.shot .urlbar .u{background:#fff;border:1px solid #e4e0d6;border-radius:7px;padding:3px 12px;color:#3a4654;font-family:var(--mono);font-size:11.5px}
.mistral{background:#faf9f5}
.mistral .in{padding:18px 20px}
.mcard{background:#fff;border:1px solid #ece8df;border-radius:14px;padding:18px 20px;max-width:420px}
.mcard .t{font-family:var(--disp);font-weight:700;font-size:16px;color:#1a1a17;margin-bottom:12px}
.mrow{display:flex;align-items:center;justify-content:space-between;padding:11px 12px;border-radius:9px;color:#3a3a35;font-size:14px}
.mrow .ar{color:#e8730a;font-weight:700}
.mrow.hot{border:2px solid #e5484d;font-weight:600;color:#1a1a17}
.dlg{background:#faf9f5;padding:20px 22px}
.dlg .box{background:#fff;border:1px solid #ece8df;border-radius:14px;padding:18px 20px;max-width:460px}
.dlg .t{font-family:var(--disp);font-weight:700;font-size:15px;color:#1a1a17;margin-bottom:14px}
.dlg label{display:block;font-size:12px;font-weight:600;color:#6b7280;margin-bottom:5px}
.dlg .inp{background:#fff;border:1px solid #d9d4c8;border-radius:9px;padding:9px 12px;font-size:14px;color:#1a1a17}
.dlg .foot{display:flex;justify-content:flex-end;gap:9px;margin-top:16px}
.okbox{padding:20px 22px}
.okbox .in{background:#eafaf0;border:1px solid #b7e6c7;border-radius:14px;padding:16px 18px}
.okbox .oh{display:flex;align-items:center;gap:8px;font-weight:700;color:#0f7a3d;font-size:14.5px}
.okbox .oh svg{width:17px;height:17px}
.keyrow{display:flex;align-items:center;justify-content:space-between;gap:10px;background:#fff;
  border:1px solid #cdead8;border-radius:9px;padding:11px 14px;margin-top:11px}
.keyrow code{font-family:var(--mono);font-size:13px;color:#1a1a17;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.keyrow .cp{color:#0f7a3d;font-weight:600;font-size:13px;flex:none}
.okbox .sub{font-size:12px;color:#5c7a67;margin-top:9px}
.b2{display:inline-flex;align-items:center;gap:6px;border-radius:9px;padding:8px 15px;font-size:13px;font-weight:600}
.b2 svg{width:13px;height:13px}
.b2.dark{background:#1a1a17;color:#fff}
.b2.ghost{background:var(--card);border:1px solid var(--line2);color:var(--muted)}
footer{max-width:900px;margin:0 auto;padding:20px 22px;color:var(--faint);font-size:12.5px;
  border-top:1px solid var(--line);text-align:center}
""";

    // ========================================================================
    //  Corps français
    // ========================================================================
    private const string BodyFr = """
<div class="top"><div class="row">
  <span class="logo">__LOGO__</span>
  <span class="bname">Echo<small>v{version}</small></span>
</div>
<nav class="tabnav" role="tablist">
  <button class="active" data-tab="prise-en-main">__TAB_START__ Prise en main</button>
  <button data-tab="enregistrer">__TAB_REC__ Enregistrer &amp; widget</button>
  <button data-tab="apres">__TAB_AFTER__ Après la réunion</button>
  <button data-tab="cle-api">__TAB_KEY__ Clé API Mistral</button>
  <button data-tab="depannage">__TAB_HELP__ Dépannage</button>
</nav></div>

<div class="wrap">

<section class="tabpanel active" id="prise-en-main">
  <h1>Bienvenue dans Echo</h1>
  <p class="lead">Echo enregistre vos réunions Teams (ou en présentiel), transcrit qui a dit quoi,
  et rédige un compte rendu prêt à envoyer. Aucun robot ne rejoint la réunion : tout se passe
  sur votre PC.</p>
  __M_HOME__
  <p class="figcap">L'écran d'accueil pendant un enregistrement.</p>

  <h2><span class="ic">__TAB_START__</span>Trois étapes pour démarrer</h2>
  <div class="card pad">
  <ol class="steps">
    <li class="step"><span class="n"></span><div class="bd"><h3>Branchez votre clé Mistral</h3>
      <p>C'est à faire une seule fois. L'onglet <b>« Clé API Mistral »</b> vous guide pas à pas.
      Cette clé sert à transcrire l'audio et à rédiger les comptes rendus.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Lancez un enregistrement</h3>
      <p>Cliquez sur <b>« Nouvel enregistrement »</b>, en haut de la barre latérale. Echo capte
      le son de la réunion Teams tout seul, ou votre micro en présentiel.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Arrêtez, récupérez le CR</h3>
      <p>En fin de réunion, cliquez sur <b>« Arrêter &amp; générer le CR »</b>. Echo transcrit,
      puis rédige le compte rendu. Vous le retrouvez dans la fiche de la réunion.</p></div></li>
  </ol>
  </div>

  <h2><span class="ic">__TAB_START__</span>La fenêtre principale</h2>
  <div class="card pad">
  <h3>La barre de titre</h3>
  <ul>
    <li><b>Recherche</b> (<kbd>Ctrl K</kbd>) : tapez un nom, la liste des réunions se filtre pendant
    que vous écrivez.</li>
    <li><b>Soleil / lune</b> : bascule entre thème clair et sombre. Le réglage complet, avec l'option
    « Système », est dans Paramètres → Général.</li>
    <li><b>La croix</b> ne ferme pas Echo. L'application reste active dans la <b>zone de
    notification</b>, près de l'horloge, et continue de surveiller vos réunions. Cliquez sur son
    icône pour rouvrir la fenêtre.</li>
  </ul>
  <h3>La barre latérale</h3>
  <ul>
    <li><b>Nouvel enregistrement</b> démarre une réunion. Pendant l'enregistrement, ce même bouton
    sert à l'arrêter.</li>
    <li><b>Importer</b> reprend un transcript Teams existant (.docx ou .vtt) <b>ou un fichier audio</b>
    (.mp3, .m4a, .wav, .mp4…). Echo crée la fiche de réunion et en tire un CR et un résumé.</li>
    <li><b>Dossier</b> crée un dossier de mission (un nom, une couleur) pour classer vos réunions.</li>
    <li><b>Accueil</b> et <b>Toutes les réunions</b> ouvrent la vue de synthèse et la liste complète.
    Le compteur affiche le total.</li>
    <li><b>Dossiers</b> liste vos missions. Un clic ouvre la liste filtrée sur ce dossier, le « + »
    en crée un nouveau.</li>
    <li><b>Ouvrir le dossier local</b> ouvre dans l'Explorateur le dossier Windows qui contient tous
    vos enregistrements.</li>
    <li><b>Votre nom</b>, tout en bas, ouvre les <b>Paramètres et le compte</b>.</li>
  </ul>
  <h3>L'accueil</h3>
  <ul>
    <li><b>Trois compteurs</b> : le nombre de réunions, de comptes rendus produits, et le temps total
    enregistré.</li>
    <li><b>La bannière d'enregistrement</b> apparaît pendant une réunion. Elle affiche le minuteur,
    le nom de la réunion, un bouton <b>Widget</b> (qui ramène le widget flottant s'il a été masqué)
    et <b>« Arrêter &amp; générer le CR »</b>. Les alertes éventuelles (micro, son) s'affichent à
    droite.</li>
    <li><b>Réunions récentes</b> et <b>Dossiers</b> donnent un accès direct aux fiches et aux
    missions.</li>
  </ul>
  <h3>Toutes les réunions</h3>
  <ul>
    <li><b>Les filtres</b> trient par nom, par dossier de mission, par statut et par période
    (7, 30 ou 90 jours).</li>
    <li><b>Sélectionner</b> active la sélection multiple : cochez des réunions, puis
    <b>Générer le transcript</b> (relance en lot) ou <b>Supprimer</b>. « Tout sélectionner » agit
    sur la liste filtrée.</li>
    <li><b>La corbeille</b> de chaque carte supprime cette réunion et tout son dossier.</li>
  </ul>
  <table>
    <tr><th>Statut</th><th>Ce qu'il signifie</th></tr>
    <tr><td><b>Compte rendu</b></td><td>transcript et CR générés, la fiche est complète.</td></tr>
    <tr><td><b>Enregistré</b></td><td>audio capté, mais transcription pas encore lancée ou CR pas encore rédigé.</td></tr>
    <tr><td><b>Traitement</b></td><td>transcription ou rédaction en cours.</td></tr>
    <tr><td><b>En attente de l'API</b></td><td>le fournisseur IA était saturé (429) ou indisponible. Echo réessaie tout seul dans 10 min, puis 30, puis 60. Rien à faire, l'audio est conservé.</td></tr>
    <tr><td><b>Erreur</b></td><td>une étape a échoué pour une cause qui ne se résout pas en attendant (clé refusée, modèle hors palier). La notification précise la cause. Relancez depuis la fiche, avec « Générer un CR » ou « Relancer la transcription ».</td></tr>
  </table>
  </div>

  <h2><span class="ic">__TAB_START__</span>Tous les réglages en un coup d'œil</h2>
  <div class="card pad">
  <table>
    <tr><th style="width:170px">Paramètres →</th><th>Ce que vous y trouvez</th></tr>
    <tr><td><b>Compte</b></td><td>votre nom, qui signe les comptes rendus.</td></tr>
    <tr><td><b>Enregistrement</b></td><td><b>Détection des réunions</b> (Echo propose d'enregistrer quand vous rejoignez un appel Teams), <b>Enregistrement automatique</b> (chaque réunion détectée est enregistrée sans confirmation), <b>Capture d'écran automatique</b> (les slides partagées sont photographiées), <b>Zone des captures</b> (la fenêtre Teams seule par défaut, ou tout l'écran), <b>Raccourci clavier global</b> (démarre ou arrête l'enregistrement depuis n'importe quelle application, Ctrl+Alt+R par défaut, modifiable et désactivable).</td></tr>
    <tr><td><b>Transcription</b></td><td>le moteur en vigueur (choisi dans Fournisseurs), la <b>langue de la réunion</b> (Auto, français ou anglais : la langue parlée, transmise au moteur de transcription), la <b>transcription automatique</b> en fin de réunion, et la <b>correction du vocabulaire (LLM)</b>, une seconde passe qui rattrape les termes métier déformés.</td></tr>
    <tr><td><b>Vocabulaire</b></td><td>vos termes métier et corrections, en portée générale ou par mission, l'import de fichier et le bouton <b>« Prompt à copier-coller »</b> (détaillé dans « Après la réunion »).</td></tr>
    <tr><td><b>Comptes rendus</b></td><td>le <b>CR automatique</b> dès la fin de la transcription, la <b>langue du CR</b> (la langue de rédaction, Auto = celle de la réunion), les formats produits (.eml, .md, .docx), le modèle par défaut, la création et l'édition de modèles.</td></tr>
    <tr><td><b>Intégrations</b></td><td>la clé API Mistral (stockée chiffrée) et son bouton Tester, ainsi que l'import de transcript Teams.</td></tr>
    <tr><td><b>Général</b></td><td>le thème, la langue de l'app (français ou anglais), le lancement au démarrage de Windows, l'emplacement des enregistrements, la conservation des WAV, la suppression de l'audio après N jours, les mises à jour (vérification au démarrage, « Vérifier maintenant », « Version précédente »), le guide d'aide et la version.</td></tr>
  </table>
  </div>
</section>

<section class="tabpanel" id="enregistrer">
  <h1>Enregistrer et le widget</h1>
  <p class="lead">Pendant l'enregistrement, une bannière s'affiche sur l'accueil et un petit
  <b>widget flottant</b> reste posé par-dessus vos fenêtres. Vous gardez tout à portée de main
  sans quitter la réunion.</p>

  <h2><span class="ic">__TAB_REC__</span>Le widget flottant</h2>
  <p>C'est une petite carte toujours accessible : minuteur, notes, moments clés, captures et bouton
  d'arrêt. Son en-tête affiche le <b>nom de la réunion</b> et son état, « Enregistrement en cours »
  ou une alerte s'il y a un souci. Attrapez l'en-tête pour le <b>déplacer</b>, et le coin en bas à
  droite pour <b>ajuster sa largeur</b>. La hauteur, elle, s'adapte toute seule au contenu.</p>
  <div class="figrow">
    <div>__M_WIDGET__<p class="figcap">Le widget, replié sur l'essentiel.</p></div>
    <div>__M_WNOTES__<p class="figcap">Déplié : la prise de notes en direct.</p></div>
  </div>

  <h3>Les boutons de l'en-tête</h3>
  <div class="card pad">
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Bouton</th><th>Ce qu'il fait</th></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 4h6l-1 7 3 3H7l3-3z"/><path d="M12 17v3"/></svg></span></td>
      <td><b>Épingle</b></td><td>garde le widget au premier plan, ce qui est le cas par défaut. Recliquez pour le laisser passer derrière les autres fenêtres.</td></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 5l-6 7 6 7"/></svg></span></td>
      <td><b>Enrouler en rail</b></td><td>replie le widget en une fine barre verticale qui ne garde que l'essentiel. Idéal pour libérer l'écran quand vous partagez votre écran.</td></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 12h12"/></svg></span></td>
      <td><b>Masquer</b></td><td>cache le widget. L'enregistrement, lui, continue. Le bouton « Widget » de la bannière d'accueil le fait réapparaître.</td></tr>
  </table>
  </div>

  <h3>Le corps : notes et dernière capture</h3>
  <div class="card pad">
  <ul>
    <li><b>Prendre une note</b> : cliquez sur « Cliquez pour saisir une note » (ou sur le bouton note,
    en bas), tapez votre texte, puis <kbd>Entrée</kbd>. Chaque note part dans
    <span class="mono">notes.md</span> et se retrouve dans l'onglet « Notes &amp; moments clés » de
    la réunion.</li>
    <li><b>Horodatage</b> <span class="wic acc" style="width:22px;height:22px"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg></span> :
    actif par défaut, il colle à chaque note le minutage de la réunion (« 12:40 »). Cliquez dessus
    pour écrire une note sans horodatage.</li>
    <li><b>Lier à la capture</b> <span class="wic" style="width:22px;height:22px"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 15l6-6M8 13l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 11l2-2a3.5 3.5 0 0 0-5-5l-2 2"/></svg></span> :
    rattache la note à la dernière capture d'écran (préfixe « [capture] »). Pratique pour commenter
    une slide précise.</li>
    <li><b>La vignette « Dernière capture »</b> apparaît après une capture, avec l'heure et le
    compteur. Un clic ouvre l'image.</li>
  </ul>
  </div>

  <h3>Les boutons du bas</h3>
  <div class="card pad">
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Bouton</th><th>Ce qu'il fait</th></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 4h11l5 5v11H4z"/><path d="M8 11h8M8 15h5"/></svg></span></td>
      <td><b>Notes</b></td><td>ouvre ou ferme le panneau de prise de notes.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 4v16M5 4h11l-2 4 2 4H5"/></svg></span></td>
      <td><b>Moment clé ⚑</b></td><td>marque l'instant présent d'une note « Moment clé » horodatée. Un clic suffit pour retrouver le passage plus tard.</td></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="6" width="18" height="13" rx="2"/><circle cx="12" cy="12.5" r="3"/></svg></span></td>
      <td><b>Capture d'écran</b></td><td>photographie l'écran (les slides partagées). L'image va dans l'onglet <b>Captures</b> de la réunion et s'affiche en vignette dans le widget.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="7" y="7" width="10" height="10" rx="1.5" fill="currentColor" stroke="none"/></svg></span></td>
      <td><b>Stop</b></td><td>arrête l'enregistrement et lance le traitement (transcription, puis CR et résumé si les automatismes sont actifs).</td></tr>
  </table>
  </div>

  <h3>Le mode rail (widget enroulé)</h3>
  <div class="card pad">
  <p>Enroulé, le widget devient une barre verticale d'environ 50 px : un point rouge qui pulse
  (l'enregistrement tourne), une poignée pour le déplacer, et quatre boutons.</p>
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Bouton</th><th>Ce qu'il fait</th></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 5l6 7-6 7"/></svg></span></td>
      <td><b>Dérouler</b></td><td>rouvre la carte complète (notes, vignette, minuteur).</td></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="6" width="18" height="13" rx="2"/><circle cx="12" cy="12.5" r="3"/></svg></span></td>
      <td><b>Capture</b></td><td>la même capture d'écran que sur la carte.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 4v16M5 4h11l-2 4 2 4H5"/></svg></span></td>
      <td><b>Moment clé ⚑</b></td><td>le même marquage d'instant que sur la carte.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="7" y="7" width="10" height="10" rx="1.5" fill="currentColor" stroke="none"/></svg></span></td>
      <td><b>Stop</b></td><td>arrête et traite, comme sur la carte.</td></tr>
  </table>
  </div>

  <h2><span class="ic">__TAB_REC__</span>Détection et automatismes</h2>
  <div class="card pad">
    <p>Tout se règle dans <b>Paramètres → Enregistrement</b>.</p>
    <ul>
      <li><b>Détection des réunions</b> : Echo surveille Teams et vous <b>propose</b> d'enregistrer
      dès que vous rejoignez un appel.</li>
      <li><b>Enregistrement automatique</b> : plus de question posée, chaque réunion Teams détectée
      est enregistrée (activer cette option active aussi la détection). Quand vous quittez la réunion,
      Echo s'arrête tout seul après un court délai, puis enchaîne transcription, CR et résumé selon
      vos réglages.</li>
      <li><b>Capture d'écran automatique</b> : les slides partagées sont photographiées
      régulièrement, en plus de vos captures manuelles.</li>
    </ul>
    <p>En <b>présentiel</b>, quand aucune réunion Teams n'est détectée, l'enregistrement se fait
    au micro seul. Posez le PC au centre de la table.</p>
    <div class="note">__INFO__<span>Le son des participants doit sortir sur <b>ce PC</b>, par ses
    haut-parleurs ou son casque. S'il sort ailleurs (un autre poste, une TV), Echo enregistre au
    micro seul et vous prévient.</span></div>
  </div>
</section>

<section class="tabpanel" id="apres">
  <h1>Après la réunion</h1>
  <p class="lead">Cliquez sur une réunion pour ouvrir sa <b>fiche</b>. Elle s'organise en onglets :
  Résumé, Transcription, Compte rendu, Captures, Participants et Notes.</p>

  <h2><span class="ic">__TAB_AFTER__</span>L'en-tête de la fiche</h2>
  <div class="card pad">
  <ul>
    <li><b>Le crayon</b>, à côté du titre, renomme la réunion (la fiche et les fichiers).</li>
    <li><b>L'icône dossier</b> ouvre le dossier Windows de la réunion.</li>
    <li><b>Le menu « Sans dossier »</b> affecte la réunion à un dossier de mission.</li>
    <li><b>Générer un compte rendu</b> bascule sur l'onglet CR et lance la rédaction avec le modèle
    choisi.</li>
    <li><b>Relancer la transcription</b> refait tout le pipeline depuis l'audio. Utile après un ajout
    au vocabulaire, ou si une transcription a échoué. L'audio n'est jamais réenregistré, seulement
    réanalysé.</li>
    <li><b>Partager &amp; exporter</b> ouvre le menu des fichiers, décrit plus bas.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Résumé</h2>
  <p>L'essentiel de la réunion en 5 à 10 puces courtes, fidèles au transcript, sans rien inventer.
  Il se génère à la demande (« Générer le résumé ») ou automatiquement selon vos réglages. Le lien
  <b>« Régénérer »</b> le refait après coup, par exemple une fois le vocabulaire enrichi ou les
  locuteurs identifiés.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Transcription</h2>
  <p>Le verbatim est découpé par prise de parole. Chaque tour est horodaté et attribué à son
  intervenant, lu depuis l'interface Teams (là encore, aucun bot n'entre dans la réunion).</p>
  __M_TR__
  <div class="card pad">
  <ul>
    <li><b>Recherche</b> : filtre les prises de parole en direct, noms d'intervenants compris.</li>
    <li><b>Copier</b> : copie tout le verbatim horodaté dans le presse-papiers.</li>
    <li><b>Identifier un locuteur</b> : les voix non reconnues apparaissent en « Locuteur N » avec un
    crayon. Donnez le vrai nom une fois, il s'applique à <b>toutes</b> les prises de parole de cette
    voix. Vous pouvez ensuite régénérer le CR avec les bons noms.</li>
    <li><b>▶ Écouter le passage</b> : au survol d'une prise de parole, un bouton lecture joue
    l'extrait audio correspondant, avec quelques secondes de contexte avant et après. Pratique
    pour vérifier une citation ou lever un doute.</li>
    <li><b>Lecteur de piste complète</b> : en haut de l'onglet, un lecteur audio permet d'écouter
    toute la réunion. Cliquez l'horodatage d'une prise de parole pour vous caler à ce moment.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Le vocabulaire métier</h2>
  <p>Dans <b>Paramètres → Vocabulaire</b>, listez les termes que la transcription écorche : noms de
  client, de projets, sigles, outils. Ils orientent la reconnaissance vocale et sont corrigés à la
  fois dans le transcript et dans le CR. Deux formats, une entrée par ligne : le <b>terme</b> seul,
  ou <b>variante1|variante2=&gt;Terme</b> pour remplacer des déformations connues par la bonne
  orthographe.</p>
  <p>Le bouton <b>« Prompt à copier-coller »</b> copie un prompt prêt à l'emploi. Collez-le dans
  votre assistant IA avec vos documents de mission (notes, mails, glossaire) et il vous génère la
  liste au bon format. Un vocabulaire peut être <b>général</b> ou dédié à une <b>mission</b>.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Compte rendu et les modèles</h2>
  <p>Choisissez un <b>modèle</b> et la <b>langue de sortie</b> (celle de la réunion, ou forcée en
  français, anglais, espagnol, allemand), puis cliquez sur <b>Générer</b>. Le CR s'affiche en aperçu
  et les fichiers listés sous « Fichiers générés » sont prêts à envoyer. Régénérer remplace le CR
  précédent.</p>
  __M_CR__
  <div class="card pad">
  <ul>
    <li><b>Deux modèles de base</b> : <b>Détaillé</b> (CR structuré, avec décisions, actions et points
    par sujet) ou <b>Synthèse</b> (juste l'essentiel).</li>
    <li><b>Vos modèles personnalisés</b> : dans <b>Paramètres → Comptes rendus</b>, partez d'une base,
    ajoutez des <b>consignes libres</b> (« mets les actions en tableau », « commence par les
    risques ») et un <b>exemple à imiter</b> (collez un CR dont vous aimez le style). Le modèle par
    défaut est celui qu'utilisent les automatismes.</li>
    <li><b>Les formats produits</b> : .eml (un brouillon Outlook prêt à envoyer), .md et .docx, au
    choix dans les Paramètres.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Captures</h2>
  <p>Toutes les copies d'écran de la réunion s'affichent en grille, qu'elles soient manuelles ou
  automatiques. Un clic ouvre l'image. Le crayon ajoute une note liée à cette capture, qui rejoint
  les notes de la réunion.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Participants et e-mails</h2>
  __M_PART__
  <div class="tip">__CHECKI__<span>Teams ne donne que les <b>noms</b>. Saisissez l'e-mail d'une
  personne <b>une seule fois</b> : Echo le mémorise et le réapplique à toutes les réunions où elle
  apparaît. Ces e-mails pré-remplissent le brouillon .eml du CR.</span></div>

  <h2><span class="ic">__TAB_AFTER__</span>Onglet Notes et moments clés</h2>
  <p>On y retrouve tout ce qui a été saisi pendant la réunion : les notes horodatées, les
  <b>moments clés ⚑</b> et les notes liées aux captures. Le bouton <b>Modifier</b> ouvre l'édition
  libre en markdown. Corrigez, complétez, puis <b>Enregistrez</b> : le fichier
  <span class="mono">notes.md</span> du dossier de la réunion est mis à jour.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Partager et exporter</h2>
  <p>Ce menu regroupe tout ce qui se partage.</p>
  __M_SHARE__
  <div class="card pad">
  <ul>
    <li><b>Dossier de la réunion</b> : tous les fichiers, dans l'Explorateur.</li>
    <li><b>Comptes rendus</b> : chaque fichier généré (.eml, .md, .docx) s'ouvre directement.
    Le .eml s'ouvre dans Outlook, prêt à envoyer.</li>
    <li><b>Audio</b> : le MP3 d'écoute de la réunion.</li>
    <li><b>Dossier Claude (.md)</b> : un dossier prêt pour l'analyse dans Claude, avec le contexte de
    la réunion, le transcript corrigé et les notes en markdown. Idéal pour creuser (« quelles
    objections a soulevées cette personne ? », un plan d'actions consolidé).</li>
  </ul>
  </div>

  <div class="card pad">
    <h3>Les dossiers de mission</h3>
    <p>Créez un dossier (bouton « Dossier » ou le « + » de la barre latérale : un nom, une couleur),
    puis affectez chaque réunion depuis sa fiche via le menu « Sans dossier ». Le dossier devient un
    <b>filtre</b> partout (barre latérale, « Toutes les réunions », accueil) et peut recevoir un
    <b>vocabulaire dédié</b> (Paramètres → Vocabulaire → « S'applique à »), appliqué en plus du
    vocabulaire général sur les réunions rattachées.</p>
    <h3>Importer un transcript Teams</h3>
    <p>Le bouton <b>Importer</b> (barre latérale, ou Paramètres → Intégrations) reprend un transcript
    exporté de Teams (.docx ou .vtt). Echo crée la fiche avec les participants et le verbatim, et peut
    générer un CR et un résumé. Utile pour les réunions déjà enregistrées par Teams.</p>
    <h3>Importer un fichier audio</h3>
    <p>Le même bouton <b>Importer</b> accepte un <b>fichier audio ou vidéo</b> : enregistrement de
    téléphone ou de dictaphone, note vocale, enregistrement Teams (.mp4)… Formats acceptés : .mp3,
    .m4a, .wav, .aac, .wma, .ogg, .opus, .flac, .amr, .3gp, .mp4, .mov, .mkv, .webm, .avi. Echo crée
    la réunion (nom = nom du fichier, heure = heure estimée du début de l'enregistrement), la
    transcrit avec le fournisseur choisi dans Paramètres, puis rédige le CR et le résumé. Le fichier
    d'origine n'est jamais modifié. Comme il n'y a qu'une seule piste et pas de participants Teams,
    les voix sont numérotées (« Participant 0 », « Participant 1 »…) quand le moteur sait les distinguer
    (Voxtral, Gemini) ; renommez-les ensuite depuis la fiche.</p>
  </div>
</section>

<section class="tabpanel" id="cle-api">
  <h1>Brancher la clé API Mistral</h1>
  <p class="lead">Echo confie la transcription et la rédaction des comptes rendus à
  <b>Mistral (Voxtral)</b>. Il lui faut donc une <b>clé API Mistral</b>, à renseigner une seule fois.
  C'est rapide, environ deux minutes, et gratuit à créer.</p>

  <div class="card pad">
  <ol class="steps">
    <li class="step"><span class="n"></span><div class="bd"><h3>Créez un compte Mistral</h3>
      <p>Ouvrez la console Mistral et inscrivez-vous, ou connectez-vous. Utilisez de préférence le
      compte ou l'espace de travail de l'entreprise.</p>
      <a class="btn" href="__MURL__" target="_blank" rel="noopener">Ouvrir console.mistral.ai __ARROW__</a>
    </div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Créez une clé API</h3>
      <p>Depuis l'accueil du Studio Mistral, cliquez sur <b>« Créer une clé API »</b>.</p>
      __MK1__</div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Nommez la clé et validez</h3>
      <p>Donnez-lui un nom, par exemple <span class="mono">Echo</span>, laissez le reste par défaut,
      puis cliquez sur <b>« Nouvelle clé »</b>.</p>
      __MK2__</div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Copiez la clé</h3>
      <p>Cliquez sur <b>« Copier »</b> pour récupérer la clé.</p>
      __MK3__
      <div class="warn">__WARN__<span>La clé n'est affichée qu'une seule fois. Si vous la perdez, il
      suffit d'en créer une nouvelle.</span></div></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Ouvrez Paramètres → Intégrations</h3>
      <p>Dans Echo, cliquez sur <b>votre nom, en bas à gauche</b>, puis sur l'onglet
      <b>Intégrations</b>.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Collez la clé, enregistrez, testez</h3>
      <p>Collez la clé dans <b>« Clé API Mistral »</b>, cliquez sur <b>« Enregistrer »</b> (elle est
      stockée chiffrée, via le DPAPI de Windows), puis sur <b>« Tester »</b>. Le message
      <b>« Clé valide ✓ »</b> confirme que tout est prêt.</p>
      __M_APIKEY__</div></li>
  </ol>
  </div>

  <div class="note">__INFO__<span><b>Côté coût</b> : la transcription revient à environ
  <b>0,18 $ à 0,36 $ par heure</b> de réunion. Si Mistral vous demande d'activer la facturation
  (« pay-per-use ») pour utiliser la clé, faites-le dans la console Mistral.</span></div>
</section>

<section class="tabpanel" id="depannage">
  <h1>Dépannage et fichiers</h1>

  <h2><span class="ic">__TAB_HELP__</span>Les problèmes courants</h2>
  <div class="card pad">
  <ul>
    <li><b>La transcription échoue.</b> Vérifiez la clé API (Paramètres → Intégrations →
    <b>Tester</b>) et votre crédit Mistral. Le détail est dans <span class="mono">echo.log</span>,
    dans le dossier <span class="mono">%LOCALAPPDATA%\Echo</span>.</li>
    <li><b>« API indisponible (saturé, 429) ».</b> La clé a atteint sa limite ou le service manque de
    capacité. Echo réessaie pendant environ deux minutes, puis bascule sur l'autre fournisseur si sa
    clé est renseignée (Paramètres, Fournisseurs et modèles), puis met la réunion « En attente de
    l'API » et réessaie dans 10, 30 et 60 minutes. Le transcript déjà produit n'est jamais perdu.</li>
    <li><b>« Modèle indisponible sur le palier de cette clé (403) ».</b> Une clé Mistral gratuite n'a
    pas accès à mistral-large. Dans Paramètres, Fournisseurs et modèles, choisissez un modèle listé
    marqué Free pour le CR (par exemple ministral-8b-latest) ou passez le CR sur Cloudflare.</li>
    <li><b>Cloudflare Workers AI (Whisper).</b> Troisième fournisseur, très économique (environ
    0,03 USD par heure d'audio) : dans Paramètres, Fournisseurs et modèles, renseignez l'identifiant de
    compte (page Workers AI du tableau de bord Cloudflare) et un jeton API avec la permission Workers AI,
    puis Tester. L'audio part par morceaux de 10 minutes. Whisper ne distingue pas les voix : les tours
    sont attribués d'après la piste (vous ou les participants) et les noms Teams relevés pendant la réunion.</li>
    <li><b>« Échec de la transcription sur Cloudflare : Failed to decode audio file ».</b> Le modèle
    de transcription choisi n'accepte pas le format d'envoi d'Echo. Tous les modèles Whisper de
    Workers AI n'ont pas le même contrat : <b>@cf/openai/whisper-large-v3-turbo</b> est celui
    recommandé (horodatage par segment). Dans Paramètres, Fournisseurs et modèles, Echo teste
    désormais le modèle dès que vous le choisissez et affiche le verdict sous le sélecteur.
    L'audio de la réunion est conservé : corrigez le modèle puis « Relancer la transcription ».</li>
    <li><b>Quel moteur est vraiment utilisé ?</b> Le choix se fait à un seul endroit, Paramètres,
    Fournisseurs et modèles. Les pages Transcription et Comptes rendus affichent en clair le moteur
    et le modèle en vigueur. Chaque réglage confirme son enregistrement, et le journal de l'application
    trace la valeur relue sur le disque.</li>
    <li><b>Place occupée sur le disque.</b> Paramètres, Stockage indique ce que pèsent vos
    enregistrements et les réunions les plus lourdes. Une heure de réunion pèse environ 1 Go pendant
    l'enregistrement, puis l'audio est archivé en MP3. Deux délais s'appliquent : les pistes des
    réunions transcrites, et celles des réunions restées sans transcription (délai plus long, car cet
    audio est la seule copie). Le bouton Nettoyer lance le ménage tout de suite. Les transcriptions,
    les comptes rendus, les notes et le MP3 d'écoute ne sont jamais supprimés.</li>
    <li><b>« Un enregistrement est déjà en cours ».</b> Un seul enregistrement à la fois : le bouton
    Enregistrer refuse tant que la capture précédente n'est pas arrêtée. Si le widget a disparu,
    utilisez le raccourci clavier ou l'icône près de l'horloge pour arrêter.</li>
    <li><b>« Enregistrement(s) interrompu(s) récupéré(s) ».</b> Au démarrage, Echo répare les réunions
    dont la capture n'a pas été arrêtée proprement (PC éteint, application fermée) : elles apparaissent
    dans la liste avec leur durée, prêtes à être transcrites d'un clic.</li>
    <li><b>Pas de noms de locuteurs.</b> Echo pose le réglage Teams nécessaire au premier lancement.
    Il vous suffit ensuite de <b>quitter complètement Teams une fois</b> (clic droit sur l'icône
    Teams → Quitter) puis de le rouvrir.</li>
    <li><b>Participants non détectés (affichage compact).</b> Quand la réunion est réduite en
    <b>petite vignette flottante</b>, Teams n'expose ni les participants ni qui parle : gardez la
    <b>fenêtre de réunion agrandie</b> (au moins de temps en temps). Echo vous alerte si la réunion
    reste en vignette, et rattrape les noms dès que vous l'agrandissez.</li>
    <li><b>« Son des participants indisponible ».</b> Le son de la réunion doit sortir sur
    <b>ce PC</b>, pas sur un autre poste ni une TV. L'enregistrement continue au micro seul.</li>
    <li><b>Votre voix manque au transcript.</b> Vérifiez que le bon <b>micro</b> était bien le
    périphérique par défaut de Windows pendant la réunion, et que Teams ne l'avait pas capté en
    exclusif. L'audio est conservé : « Relancer la transcription » réessaie sans rien perdre.</li>
    <li><b>Le widget a disparu.</b> Il a été masqué ou enroulé hors écran. Le bouton <b>Widget</b> de
    la bannière d'accueil le ramène, pendant un enregistrement.</li>
    <li><b>La fenêtre a disparu.</b> Echo est toujours actif dans la <b>zone de notification</b>
    (la flèche « afficher les icônes masquées »). Cliquez sur l'icône pour rouvrir la fenêtre.</li>
    <li><b>Mettre à jour Echo.</b> Paramètres → Général → <b>« Vérifier maintenant »</b>. Si quelque
    chose coince après une mise à jour, <b>« Version précédente »</b> restaure l'ancienne
    installation.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Les fichiers produits</h2>
  <p class="lead">Chaque réunion crée un dossier dans
  <span class="mono">%LOCALAPPDATA%\Echo\recordings</span> (bouton « Ouvrir le dossier local »).</p>
  <div class="card pad">
  <table>
    <tr><th>Fichier</th><th>Contenu</th></tr>
    <tr><td><b>… - Audio.mp3</b></td><td>l'enregistrement audio</td></tr>
    <tr><td><b>… - Transcript.txt</b></td><td>la transcription brute horodatée</td></tr>
    <tr><td><b>… - Processed Transcript.txt</b></td><td>la transcription attribuée par locuteur</td></tr>
    <tr><td><b>… - Minutes.eml / .md / .docx</b></td><td>le compte rendu, prêt à envoyer dans Outlook</td></tr>
    <tr><td><b>notes.md</b></td><td>vos notes et moments clés horodatés</td></tr>
    <tr><td><b>Captures/</b></td><td>les captures d'écran du partage</td></tr>
    <tr><td><b>mic.mp3 / system.mp3</b></td><td>les pistes brutes archivées (votre micro, le son des participants), qui permettent de retranscrire</td></tr>
    <tr><td><b>Infos réunion.txt</b></td><td>un récapitulatif lisible (date, durée, participants)</td></tr>
  </table>
  <div class="note">__INFO__<span>Après transcription, l'audio brut (WAV, environ 1 Go par heure) est
  archivé en MP3 par piste, à peu près 20 fois plus léger. La re-transcription reste possible. Dans
  les Paramètres, « Conserver les WAV originaux » désactive cet archivage, et « Suppression de
  l'audio (jours) » purge l'audio des réunions transcrites après N jours. Les transcriptions,
  comptes rendus et MP3 d'écoute, eux, sont toujours conservés.</span></div>
  </div>
</section>

</div>
<footer>Echo v{version}</footer>
""";

    // ========================================================================
    //  English body
    // ========================================================================
    private const string BodyEn = """
<div class="top"><div class="row">
  <span class="logo">__LOGO__</span>
  <span class="bname">Echo<small>v{version}</small></span>
</div>
<nav class="tabnav" role="tablist">
  <button class="active" data-tab="prise-en-main">__TAB_START__ Getting started</button>
  <button data-tab="enregistrer">__TAB_REC__ Record &amp; widget</button>
  <button data-tab="apres">__TAB_AFTER__ After the meeting</button>
  <button data-tab="cle-api">__TAB_KEY__ Mistral API key</button>
  <button data-tab="depannage">__TAB_HELP__ Troubleshooting</button>
</nav></div>

<div class="wrap">

<section class="tabpanel active" id="prise-en-main">
  <h1>Welcome to Echo</h1>
  <p class="lead">Echo records your Teams (or in-person) meetings, transcribes who said what, and
  writes ready-to-send minutes. No bot joins the meeting: everything happens on your PC.</p>
  __M_HOME__
  <p class="figcap">The home screen during a recording.</p>

  <h2><span class="ic">__TAB_START__</span>Three steps to get going</h2>
  <div class="card pad">
  <ol class="steps">
    <li class="step"><span class="n"></span><div class="bd"><h3>Connect your Mistral key</h3>
      <p>You do this once. The <b>"Mistral API key"</b> tab walks you through it. The key is what
      powers transcription and minutes.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Start a recording</h3>
      <p>Click <b>"New recording"</b> at the top of the sidebar. Echo captures the Teams meeting
      sound on its own, or your mic for in-person meetings.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Stop, get the minutes</h3>
      <p>At the end, click <b>"Stop &amp; generate minutes"</b>. Echo transcribes, then writes the
      minutes. You will find them in the meeting page.</p></div></li>
  </ol>
  </div>

  <h2><span class="ic">__TAB_START__</span>The main window</h2>
  <div class="card pad">
  <h3>The title bar</h3>
  <ul>
    <li><b>Search</b> (<kbd>Ctrl K</kbd>): type a name and the meeting list filters as you write.</li>
    <li><b>Sun / moon</b>: switches between light and dark theme. The full setting, including
    "System", lives in Settings → General.</li>
    <li><b>The close button</b> does not quit Echo. The app keeps running in the <b>notification
    area</b>, near the clock, and keeps watching your meetings. Click its icon to reopen the
    window.</li>
  </ul>
  <h3>The sidebar</h3>
  <ul>
    <li><b>New recording</b> starts a meeting. While recording, the same button stops it.</li>
    <li><b>Import</b> reuses an existing Teams transcript (.docx or .vtt) <b>or an audio file</b>
    (.mp3, .m4a, .wav, .mp4…). Echo creates the meeting page and produces minutes and a summary.</li>
    <li><b>Folder</b> creates a mission folder (a name, a colour) to organise your meetings.</li>
    <li><b>Home</b> and <b>All meetings</b> open the overview and the full list. The counter shows
    the total.</li>
    <li><b>Folders</b> lists your missions. A click opens the list filtered on that folder, the "+"
    creates a new one.</li>
    <li><b>Open local folder</b> opens the Windows folder that holds every recording.</li>
    <li><b>Your name</b>, at the very bottom, opens <b>Settings and account</b>.</li>
  </ul>
  <h3>Home</h3>
  <ul>
    <li><b>Three counters</b>: the number of meetings, minutes produced, and total recorded time.</li>
    <li><b>The recording banner</b> shows during a meeting. It has the timer, the meeting name, a
    <b>Widget</b> button (which brings the floating widget back if hidden) and <b>"Stop &amp;
    generate minutes"</b>. Any warnings (mic, sound) appear on the right.</li>
    <li><b>Recent meetings</b> and <b>Folders</b> give direct access to pages and missions.</li>
  </ul>
  <h3>All meetings</h3>
  <ul>
    <li><b>The filters</b> sort by name, mission folder, status and period (7, 30 or 90 days).</li>
    <li><b>Select</b> turns on multi-selection: tick meetings, then <b>Generate transcript</b>
    (batch re-run) or <b>Delete</b>. "Select all" acts on the filtered list.</li>
    <li><b>The trash icon</b> on each card deletes that meeting and its whole folder.</li>
  </ul>
  <table>
    <tr><th>Status</th><th>What it means</th></tr>
    <tr><td><b>Minutes</b></td><td>transcript and minutes generated, the page is complete.</td></tr>
    <tr><td><b>Recorded</b></td><td>audio captured, but transcription not run yet or no minutes yet.</td></tr>
    <tr><td><b>Processing</b></td><td>transcription or writing in progress.</td></tr>
    <tr><td><b>Waiting for API</b></td><td>the AI provider was saturated (429) or down. Echo retries by itself after 10 min, then 30, then 60. Nothing to do, the audio is kept.</td></tr>
    <tr><td><b>Error</b></td><td>a step failed for a cause that waiting will not fix (key refused, model outside the tier). The notification names the cause. Re-run from the page with "Generate minutes" or "Re-run transcription".</td></tr>
  </table>
  </div>

  <h2><span class="ic">__TAB_START__</span>Every setting at a glance</h2>
  <div class="card pad">
  <table>
    <tr><th style="width:170px">Settings →</th><th>What you find there</th></tr>
    <tr><td><b>Account</b></td><td>your name, which signs the minutes.</td></tr>
    <tr><td><b>Recording</b></td><td><b>Meeting detection</b> (Echo offers to record when you join a Teams call), <b>Automatic recording</b> (every detected meeting is recorded without confirmation), <b>Automatic screenshots</b> (shared slides are captured), <b>Screenshot area</b> (the Teams window only by default, or the whole screen), <b>Global keyboard shortcut</b> (start or stop recording from any application, Ctrl+Alt+R by default, editable and can be turned off).</td></tr>
    <tr><td><b>Transcription</b></td><td>the engine in force (chosen under Providers), the <b>meeting language</b> (Auto, French or English: the spoken language, passed to the transcription engine), <b>automatic transcription</b> at the end of the meeting, and <b>Vocabulary correction (LLM)</b>, a second pass that fixes mangled business terms.</td></tr>
    <tr><td><b>Vocabulary</b></td><td>your business terms and corrections, general or per-mission scope, file import, and the <b>"Copy-paste prompt"</b> button (covered under "After the meeting").</td></tr>
    <tr><td><b>Minutes</b></td><td>the <b>automatic minutes</b> right after transcription, the <b>minutes language</b> (the language they are written in, Auto = the meeting's language), output formats (.eml, .md, .docx), the default template, template creation and editing.</td></tr>
    <tr><td><b>Integrations</b></td><td>the Mistral API key (stored encrypted) and its Test button, plus Teams transcript import.</td></tr>
    <tr><td><b>General</b></td><td>theme, app language (French or English), launch at Windows startup, recordings location, keeping original WAVs, audio deletion after N days, updates (check on startup, "Check now", "Previous version"), the help guide and the version.</td></tr>
  </table>
  </div>
</section>

<section class="tabpanel" id="enregistrer">
  <h1>Recording and the widget</h1>
  <p class="lead">While recording, a banner shows on the home screen and a small <b>floating
  widget</b> stays on top of your windows. You keep everything at hand without leaving the
  meeting.</p>

  <h2><span class="ic">__TAB_REC__</span>The floating widget</h2>
  <p>It is a small card that stays reachable: timer, notes, key moments, screenshots and a stop
  button. Its header shows the <b>meeting name</b> and its state, "Recording in progress" or a
  warning if something is off. Grab the header to <b>move</b> it, and the bottom-right corner to
  <b>adjust its width</b>. The height adjusts itself to the content.</p>
  <div class="figrow">
    <div>__M_WIDGET__<p class="figcap">The widget, folded to the essentials.</p></div>
    <div>__M_WNOTES__<p class="figcap">Unfolded: live note-taking.</p></div>
  </div>

  <h3>Header buttons</h3>
  <div class="card pad">
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Button</th><th>What it does</th></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 4h6l-1 7 3 3H7l3-3z"/><path d="M12 17v3"/></svg></span></td>
      <td><b>Pin</b></td><td>keeps the widget on top, which is the default. Click again to let it go behind other windows.</td></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 5l-6 7 6 7"/></svg></span></td>
      <td><b>Fold into rail</b></td><td>collapses the widget into a thin vertical bar that keeps only the essentials. Great to free up the screen while presenting.</td></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 12h12"/></svg></span></td>
      <td><b>Hide</b></td><td>hides the widget. Recording keeps going. The "Widget" button in the home banner brings it back.</td></tr>
  </table>
  </div>

  <h3>The body: notes and last capture</h3>
  <div class="card pad">
  <ul>
    <li><b>Take a note</b>: click "Click to type a note" (or the note button at the bottom), type,
    then press <kbd>Enter</kbd>. Every note goes to <span class="mono">notes.md</span> and shows up
    in the meeting's "Notes &amp; key moments" tab.</li>
    <li><b>Timestamp</b> <span class="wic acc" style="width:22px;height:22px"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg></span>:
    on by default, it stamps each note with the meeting time ("12:40"). Click it to write a note
    without a timestamp.</li>
    <li><b>Link to capture</b> <span class="wic" style="width:22px;height:22px"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 15l6-6M8 13l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 11l2-2a3.5 3.5 0 0 0-5-5l-2 2"/></svg></span>:
    attaches the note to the last screenshot (a "[capture]" prefix). Handy to comment a specific
    slide.</li>
    <li><b>The "Last capture" thumbnail</b> appears after a screenshot, with the time and a counter.
    A click opens the image.</li>
  </ul>
  </div>

  <h3>Bottom buttons</h3>
  <div class="card pad">
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Button</th><th>What it does</th></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 4h11l5 5v11H4z"/><path d="M8 11h8M8 15h5"/></svg></span></td>
      <td><b>Notes</b></td><td>opens or closes the note-taking panel.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 4v16M5 4h11l-2 4 2 4H5"/></svg></span></td>
      <td><b>Key moment ⚑</b></td><td>marks the current instant with a time-stamped "Key moment" note. One click, to find the passage later.</td></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="6" width="18" height="13" rx="2"/><circle cx="12" cy="12.5" r="3"/></svg></span></td>
      <td><b>Screenshot</b></td><td>captures the screen (the shared slides). The image goes to the meeting's <b>Screens</b> tab and shows as a thumbnail in the widget.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="7" y="7" width="10" height="10" rx="1.5" fill="currentColor" stroke="none"/></svg></span></td>
      <td><b>Stop</b></td><td>stops the recording and starts processing (transcription, then minutes and summary if the automations are on).</td></tr>
  </table>
  </div>

  <h3>Rail mode (folded widget)</h3>
  <div class="card pad">
  <p>Folded, the widget becomes a vertical bar about 50 px wide: a pulsing red dot (recording is
  running), a handle to move it, and four buttons.</p>
  <table>
    <tr><th style="width:44px"></th><th style="width:150px">Button</th><th>What it does</th></tr>
    <tr><td><span class="wic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 5l6 7-6 7"/></svg></span></td>
      <td><b>Unfold</b></td><td>reopens the full card (notes, thumbnail, timer).</td></tr>
    <tr><td><span class="wic acc"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="6" width="18" height="13" rx="2"/><circle cx="12" cy="12.5" r="3"/></svg></span></td>
      <td><b>Screenshot</b></td><td>the same screen capture as on the card.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 4v16M5 4h11l-2 4 2 4H5"/></svg></span></td>
      <td><b>Key moment ⚑</b></td><td>the same instant marking as on the card.</td></tr>
    <tr><td><span class="wic rec"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="7" y="7" width="10" height="10" rx="1.5" fill="currentColor" stroke="none"/></svg></span></td>
      <td><b>Stop</b></td><td>stops and processes, as on the card.</td></tr>
  </table>
  </div>

  <h2><span class="ic">__TAB_REC__</span>Detection and automations</h2>
  <div class="card pad">
    <p>Everything is set in <b>Settings → Recording</b>.</p>
    <ul>
      <li><b>Meeting detection</b>: Echo watches Teams and <b>offers</b> to record as soon as you
      join a call.</li>
      <li><b>Automatic recording</b>: no prompt at all, every detected Teams meeting is recorded
      (turning this on also enables detection). When you leave the meeting, Echo stops by itself
      after a short delay, then chains transcription, minutes and summary based on your settings.</li>
      <li><b>Automatic screenshots</b>: shared slides are captured regularly, on top of your manual
      captures.</li>
    </ul>
    <p><b>In-person</b>, when no Teams meeting is detected, recording is mic-only. Put the PC in the
    middle of the table.</p>
    <div class="note">__INFO__<span>The participants' sound must come out of <b>this PC</b>, through
    its speakers or headset. If it comes out elsewhere (another computer, a TV), Echo records
    mic-only and warns you.</span></div>
  </div>
</section>

<section class="tabpanel" id="apres">
  <h1>After the meeting</h1>
  <p class="lead">Click a meeting to open its <b>page</b>. It is organised in tabs: Summary,
  Transcript, Minutes, Screens, Attendees and Notes.</p>

  <h2><span class="ic">__TAB_AFTER__</span>The page header</h2>
  <div class="card pad">
  <ul>
    <li><b>The pencil</b>, next to the title, renames the meeting (the page and the files).</li>
    <li><b>The folder icon</b> opens the meeting's Windows folder.</li>
    <li><b>The "No folder" menu</b> assigns the meeting to a mission folder.</li>
    <li><b>Generate minutes</b> switches to the Minutes tab and starts writing with the chosen
    template.</li>
    <li><b>Re-run transcription</b> redoes the whole pipeline from the audio. Useful after a
    vocabulary addition, or if a transcription failed. The audio is never re-recorded, only
    re-analysed.</li>
    <li><b>Share &amp; export</b> opens the files menu, described below.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Summary tab</h2>
  <p>The gist of the meeting in 5 to 10 short bullets, faithful to the transcript, nothing invented.
  It is generated on demand ("Generate summary") or automatically per your settings. The
  <b>"Regenerate"</b> link redoes it afterwards, for example once the vocabulary is enriched or the
  speakers identified.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Transcript tab</h2>
  <p>The verbatim is split by speaking turn. Each turn is time-stamped and attributed to its speaker,
  read from the Teams interface (again, no bot enters the meeting).</p>
  __M_TR__
  <div class="card pad">
  <ul>
    <li><b>Search</b>: filters turns live, speaker names included.</li>
    <li><b>Copy</b>: copies the whole time-stamped verbatim to the clipboard.</li>
    <li><b>Identify a speaker</b>: unrecognised voices show as "Speaker N" with a pencil. Give the
    real name once and it applies to <b>all</b> turns of that voice. You can then regenerate the
    minutes with proper names.</li>
    <li><b>▶ Listen to the passage</b>: hover a turn and a play button plays the matching audio
    excerpt, with a few seconds of context before and after. Handy to check a quote or settle a doubt.</li>
    <li><b>Full-track player</b>: at the top of the tab, an audio player lets you listen to the
    whole meeting. Click a turn's timestamp to jump to that moment.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Business vocabulary</h2>
  <p>In <b>Settings → Vocabulary</b>, list the terms the transcription tends to mangle: client names,
  projects, acronyms, tools. They steer speech recognition and get corrected in both the transcript
  and the minutes. Two formats, one entry per line: a bare <b>term</b>, or
  <b>variant1|variant2=&gt;Term</b> to replace known distortions with the right spelling.</p>
  <p>The <b>"Copy-paste prompt"</b> button copies a ready-made prompt. Paste it into your AI
  assistant along with your mission documents (notes, e-mails, glossary) and it generates the list
  in the right format. A vocabulary can be <b>general</b> or dedicated to a <b>mission</b>.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Minutes tab and templates</h2>
  <p>Pick a <b>template</b> and the <b>output language</b> (the meeting's language, or forced to
  French, English, Spanish or German), then click <b>Generate</b>. The minutes preview in the tab
  and the files listed under "Generated files" are ready to send. Regenerating replaces the previous
  minutes.</p>
  __M_CR__
  <div class="card pad">
  <ul>
    <li><b>Two base templates</b>: <b>Detailed</b> (structured minutes, with decisions, actions and
    points per topic) or <b>Summary</b> (just the essentials).</li>
    <li><b>Your custom templates</b>: in <b>Settings → Minutes</b>, start from a base, add <b>free
    guidance</b> ("put actions in a table", "start with the risks") and a <b>sample to imitate</b>
    (paste minutes whose style you like). The default template is the one the automations use.</li>
    <li><b>Output formats</b>: .eml (a ready-to-send Outlook draft), .md and .docx, your pick in
    Settings.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>Screens tab</h2>
  <p>Every screenshot of the meeting shows in a grid, whether manual or automatic. A click opens the
  image. The pencil adds a note linked to that capture, which joins the meeting notes.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Attendees and e-mails tab</h2>
  __M_PART__
  <div class="tip">__CHECKI__<span>Teams only gives <b>names</b>. Enter a person's e-mail <b>once</b>:
  Echo remembers it and re-applies it to every meeting where they appear. These e-mails pre-fill the
  minutes' .eml draft.</span></div>

  <h2><span class="ic">__TAB_AFTER__</span>Notes and key moments tab</h2>
  <p>Here you find everything typed during the meeting: the time-stamped notes, the
  <b>key moments ⚑</b> and the capture-linked notes. The <b>Edit</b> button opens free markdown
  editing. Fix, complete, then <b>Save</b>: the meeting folder's <span class="mono">notes.md</span>
  is updated.</p>

  <h2><span class="ic">__TAB_AFTER__</span>Share and export</h2>
  <p>This menu gathers everything shareable.</p>
  __M_SHARE__
  <div class="card pad">
  <ul>
    <li><b>Meeting folder</b>: all the files, in Explorer.</li>
    <li><b>Minutes</b>: each generated file (.eml, .md, .docx) opens directly. The .eml opens in
    Outlook, ready to send.</li>
    <li><b>Audio</b>: the meeting's listening MP3.</li>
    <li><b>Claude folder (.md)</b>: a folder ready for analysis in Claude, with the meeting context,
    the corrected transcript and the notes in markdown. Great for digging further ("what objections
    did this person raise?", a consolidated action plan).</li>
  </ul>
  </div>

  <div class="card pad">
    <h3>Mission folders</h3>
    <p>Create a folder ("Folder" button or the sidebar "+": a name, a colour), then assign each
    meeting from its page via the "No folder" menu. The folder becomes a <b>filter</b> everywhere
    (sidebar, "All meetings", home) and can get a <b>dedicated vocabulary</b>
    (Settings → Vocabulary → "Applies to"), applied on top of the general one to the meetings linked
    to it.</p>
    <h3>Import a Teams transcript</h3>
    <p>The <b>Import</b> button (sidebar, or Settings → Integrations) reuses a transcript exported
    from Teams (.docx or .vtt). Echo creates the page with attendees and verbatim, and can generate
    minutes and a summary. Useful for meetings already recorded by Teams.</p>
    <h3>Import an audio file</h3>
    <p>The same <b>Import</b> button accepts an <b>audio or video file</b>: phone or dictaphone
    recording, voice note, Teams recording (.mp4)… Accepted formats: .mp3, .m4a, .wav, .aac, .wma,
    .ogg, .opus, .flac, .amr, .3gp, .mp4, .mov, .mkv, .webm, .avi. Echo creates the meeting (name =
    file name, time = estimated start of the recording), transcribes it with the provider chosen in
    Settings, then writes the minutes and the summary. The original file is never modified. As there
    is a single track and no Teams attendees, voices are numbered ("Participant 0", "Participant 1"…)
    when the engine can tell them apart (Voxtral, Gemini); rename them afterwards from the page.</p>
  </div>
</section>

<section class="tabpanel" id="cle-api">
  <h1>Connect the Mistral API key</h1>
  <p class="lead">Echo relies on <b>Mistral (Voxtral)</b> for transcription and minutes. So it needs
  a <b>Mistral API key</b>, entered once. It is quick, about two minutes, and free to create.</p>

  <div class="card pad">
  <ol class="steps">
    <li class="step"><span class="n"></span><div class="bd"><h3>Create a Mistral account</h3>
      <p>Open the Mistral console and sign up, or sign in. Prefer your company account or
      workspace.</p>
      <a class="btn" href="__MURL__" target="_blank" rel="noopener">Open console.mistral.ai __ARROW__</a>
    </div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Create an API key</h3>
      <p>From the Mistral Studio home, click <b>"Create an API key"</b>.</p>
      __MK1__</div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Name the key and confirm</h3>
      <p>Give it a name, for example <span class="mono">Echo</span>, leave the rest as default, then
      click <b>"Create new key"</b>.</p>
      __MK2__</div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Copy the key</h3>
      <p>Click <b>"Copy"</b> to grab the key.</p>
      __MK3__
      <div class="warn">__WARN__<span>The key is shown only once. If you lose it, just create a new
      one.</span></div></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Open Settings → Integrations</h3>
      <p>In Echo, click <b>your name, bottom-left</b>, then the <b>Integrations</b> tab.</p></div></li>
    <li class="step"><span class="n"></span><div class="bd"><h3>Paste, save, test</h3>
      <p>Paste the key into <b>"Mistral API key"</b>, click <b>"Save"</b> (it is stored encrypted,
      via Windows DPAPI), then <b>"Test"</b>. A <b>"Key valid ✓"</b> message confirms everything is
      ready.</p>
      __M_APIKEY__</div></li>
  </ol>
  </div>

  <div class="note">__INFO__<span><b>On cost</b>: transcription runs about <b>$0.18 to $0.36 per
  hour</b> of meeting. If Mistral asks you to enable billing ("pay-per-use") to use the key, do it
  in the Mistral console.</span></div>
</section>

<section class="tabpanel" id="depannage">
  <h1>Troubleshooting and files</h1>

  <h2><span class="ic">__TAB_HELP__</span>Common issues</h2>
  <div class="card pad">
  <ul>
    <li><b>Transcription fails.</b> Check the API key (Settings → Integrations → <b>Test</b>) and
    your Mistral credit. Details are in <span class="mono">echo.log</span>, in the
    <span class="mono">%LOCALAPPDATA%\Echo</span> folder.</li>
    <li><b>"API unavailable (saturated, 429)".</b> The key hit its limit or the service lacks
    capacity. Echo retries for about two minutes, then switches to the other provider if its key is
    set (Settings, Providers and models), then marks the meeting "Waiting for API" and retries after
    10, 30 and 60 minutes. An already-produced transcript is never lost.</li>
    <li><b>"Model not available on this key's tier (403)".</b> A free Mistral key cannot use
    mistral-large. In Settings, Providers and models, pick a model listed as available for the
    minutes tagged Free (for example ministral-8b-latest) or switch the minutes to Cloudflare.</li>
    <li><b>Cloudflare Workers AI (Whisper).</b> Third provider, very cheap (about 0.03 USD per hour
    of audio): in Settings, Providers and models, enter the account id (Workers AI page of the
    Cloudflare dashboard) and an API token with the Workers AI permission, then Test. The audio is
    sent in 10 minute pieces. Whisper does not separate voices: turns are attributed from the track
    (you or the participants) and the Teams names picked up during the meeting.</li>
    <li><b>"Transcription failed on Cloudflare: Failed to decode audio file".</b> The chosen
    transcription model does not accept the request format Echo sends. Workers AI Whisper models do
    not share one contract: <b>@cf/openai/whisper-large-v3-turbo</b> is the recommended one (segment
    timestamps). Under Settings, Providers and models, Echo now tests the model as soon as you pick
    it and shows the verdict under the selector. The meeting audio is kept: fix the model, then
    "Retry transcription".</li>
    <li><b>Which engine is actually used?</b> The choice is made in one place only, Settings,
    Providers and models. The Transcription and Minutes pages state the engine and model in force.
    Every setting confirms that it was saved, and the application log traces the value read back
    from disk.</li>
    <li><b>Disk space.</b> Settings, Storage shows what your recordings weigh and the heaviest
    meetings. An hour of meeting weighs about 1 GB while recording, then the audio is archived to
    MP3. Two delays apply: the tracks of transcribed meetings, and those of meetings left without a
    transcript (a longer delay, since that audio is the only copy). The Clean up button runs it now.
    Transcripts, minutes, notes and the listening MP3 are never deleted.</li>
    <li><b>"A recording is already running".</b> One recording at a time: the Record button refuses
    until the previous capture is stopped. If the widget vanished, use the keyboard shortcut or the
    tray icon to stop.</li>
    <li><b>"Interrupted recording(s) recovered".</b> At startup, Echo repairs meetings whose capture
    was not stopped cleanly (PC shut down, app closed): they show up in the list with their
    duration, ready to transcribe in one click.</li>
    <li><b>No speaker names.</b> Echo sets the required Teams flag on first launch. You then just
    need to <b>fully quit Teams once</b> (right-click the Teams icon → Quit) and reopen it.</li>
    <li><b>Participants not detected (compact view).</b> When the meeting is shrunk to the
    <b>small floating tile</b>, Teams exposes neither the participants nor who is speaking: keep the
    <b>meeting window enlarged</b> (at least from time to time). Echo alerts you if the meeting stays
    in the tile, and picks the names up as soon as you enlarge it.</li>
    <li><b>"Participants' audio unavailable".</b> The meeting sound must come out of <b>this PC</b>,
    not another computer or a TV. Recording continues mic-only.</li>
    <li><b>Your voice is missing from the transcript.</b> Check that the right <b>microphone</b> was
    Windows' default device during the meeting, and that Teams had not grabbed it exclusively. The
    audio is kept: "Re-run transcription" retries without losing anything.</li>
    <li><b>The widget disappeared.</b> It was hidden or folded off-screen. The <b>Widget</b> button
    in the home banner brings it back, during a recording.</li>
    <li><b>The window disappeared.</b> Echo is still running in the <b>notification area</b> (the
    "show hidden icons" arrow). Click the icon to reopen the window.</li>
    <li><b>Updating Echo.</b> Settings → General → <b>"Check now"</b>. If something goes wrong after
    an update, <b>"Previous version"</b> restores the old install.</li>
  </ul>
  </div>

  <h2><span class="ic">__TAB_AFTER__</span>The output files</h2>
  <p class="lead">Each meeting creates a folder under
  <span class="mono">%LOCALAPPDATA%\Echo\recordings</span> ("Open local folder" button).</p>
  <div class="card pad">
  <table>
    <tr><th>File</th><th>Content</th></tr>
    <tr><td><b>… - Audio.mp3</b></td><td>the audio recording</td></tr>
    <tr><td><b>… - Transcript.txt</b></td><td>the raw time-stamped transcription</td></tr>
    <tr><td><b>… - Processed Transcript.txt</b></td><td>the speaker-attributed transcription</td></tr>
    <tr><td><b>… - Minutes.eml / .md / .docx</b></td><td>the minutes, ready to send in Outlook</td></tr>
    <tr><td><b>notes.md</b></td><td>your time-stamped notes and key moments</td></tr>
    <tr><td><b>Captures/</b></td><td>the screenshots of the shared screen</td></tr>
    <tr><td><b>mic.mp3 / system.mp3</b></td><td>the archived raw tracks (your mic, the participants' sound), used to re-transcribe</td></tr>
    <tr><td><b>Infos réunion.txt</b></td><td>a readable recap (date, duration, attendees)</td></tr>
  </table>
  <div class="note">__INFO__<span>After transcription, the raw audio (WAV, about 1 GB per hour) is
  archived to per-track MP3s, roughly 20 times smaller. Re-transcription still works. In Settings,
  "Keep original WAVs" turns this archiving off, and "Audio deletion (days)" purges transcribed
  meetings' audio after N days. Transcripts, minutes and the listening MP3 are always kept.</span></div>
  </div>
</section>

</div>
<footer>Echo v{version}</footer>
""";
}
