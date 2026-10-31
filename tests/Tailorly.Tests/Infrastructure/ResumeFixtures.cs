using System.IO.Compression;
using System.Security;
using System.Text;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Twelve synthetic résumés for the format-preservation suite (PRD §14.1), written as Word writes them: raw OPC packages
/// with rsids and <c>w14:paraId</c>s, spell-check marks splitting runs, a theme, a font table, settings, document
/// properties, headers and footers, fields, drawings, content controls and section breaks inside paragraphs. All people,
/// employers and contact details are invented. No real résumé is committed (the opt-in local-folder test covers those).
/// </summary>
public static class ResumeFixtures
{
    public sealed record Fixture(string Name, string Features, byte[] Docx);

    public static IReadOnlyList<Fixture> All { get; } =
    [
        new("classic-single-column", "Calibri, heading styles, numbered bullets, title/date lines with a right tab, header text", Classic()),
        new("two-column-sections", "continuous section break, two-column section, keep-with-next, spacing", TwoColumn()),
        new("sidebar-layout-table", "two-cell layout table with contact and skills in the sidebar, bullets inside cells", SidebarTable()),
        new("typed-bullets-bold-leadins", "typed bullet glyphs, bold lead-in labels, a bold keyword mid-sentence, italics", TypedBullets()),
        new("theme-fonts-spellcheck", "theme fonts, colours, small caps, character spacing, runs split by spell-check marks", ThemeFonts()),
        new("hyperlinks-header-footer", "hyperlinks in contact and a project, header with email and phone, PAGE field in the footer", Hyperlinks()),
        new("textbox-and-photo", "floating text box (DrawingML with VML fallback), photo in the header", TextBoxPhoto()),
        new("skills-grid-table", "skills grid table with bold labels per cell, certifications list", SkillsGrid()),
        new("multilevel-lists", "multi-level bullets (levels 0 and 1), a decimal numbered list, direct numbering", MultiLevel()),
        new("content-controls-template", "template content controls around contact and summary, multiple fonts", ContentControls()),
        new("unicode-special-characters", "curly quotes, en/em dashes, ₹, non-breaking and soft hyphens, smart tag, Devanagari, escapes", UnicodeSpecial()),
        new("google-docs-sections", "Google Docs style direct formatting, section break inside a bullet, page break, bookmarks, rendered page breaks", GoogleDocsSections()),
    ];

    public static Fixture Get(string name) => All.Single(f => f.Name == name);

    // =====================================================================================================================
    // XML helpers (Word's element order is followed so the originals validate)
    // =====================================================================================================================

    private static string X(string text) => SecurityElement.Escape(text);

    private static int _paraId = 0x10A0B000;

    private static string ParaAttrs(bool word = true) => word
        ? $" w14:paraId=\"{Interlocked.Increment(ref _paraId):X8}\" w14:textId=\"77777777\" w:rsidR=\"00A12B34\" w:rsidRDefault=\"00A12B34\" w:rsidP=\"00C56D78\""
        : "";

    /// <summary>A paragraph with optional properties (already in schema order) and content.</summary>
    private static string P(string pPr, params string[] content) =>
        $"<w:p{ParaAttrs()}>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}{string.Concat(content)}</w:p>";

    /// <summary>A paragraph as Google Docs writes it (no rsids or paragraph ids).</summary>
    private static string Pg(string pPr, params string[] content) =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}{string.Concat(content)}</w:p>";

    /// <summary>A run; <paramref name="rPr"/> is already in schema order.</summary>
    private static string R(string text, string rPr = "", string rsid = "00E91F02") =>
        $"<w:r w:rsidRPr=\"{rsid}\">{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";

    private static string Rg(string text, string rPr) => $"<w:r><w:rPr>{rPr}</w:rPr><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";

    private const string B = "<w:b/><w:bCs/>";
    private const string I = "<w:i/><w:iCs/>";
    private static string Style(string id) => $"<w:pStyle w:val=\"{id}\"/>";
    private static string Num(int numId, int level = 0) => $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr>";
    private const string Bullet = "<w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>";
    private const string RightTab = "<w:tabs><w:tab w:val=\"right\" w:pos=\"9638\"/></w:tabs>";
    private static string TitleDate(string left, string right) =>
        P(RightTab, R(left, B), $"<w:r><w:rPr>{B}</w:rPr><w:tab/><w:t xml:space=\"preserve\">{X(right)}</w:t></w:r>");
    private static string Spell(string open = "spellStart") => $"<w:proofErr w:type=\"{open}\"/>";

    private const string SectA4 =
        "<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/>";

    private static string Body(string content, string sectPr) =>
        $"<w:body>{content}<w:sectPr w:rsidR=\"00A12B34\">{sectPr}</w:sectPr></w:body>";

    // =====================================================================================================================
    // Fixtures
    // =====================================================================================================================

    private static byte[] Classic() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Rohan Mehta")),
            P("", R("Pune, India · rohan.mehta@example.test · +91 98000 11111")),
            P(Style("Heading1"), R("Professional Summary")),
            P("", R("Backend engineer with 7 years of experience building Java and Spring Boot services for logistics and retail platforms, with a focus on reliability and clean APIs.")),
            P(Style("Heading1"), R("Experience")),
            TitleDate("Senior Software Engineer, Cargoline Logistics", "Mar 2021 – Present"),
            P(Bullet, R("Designed shipment tracking APIs in Java and Spring Boot used by 40 warehouse teams.")),
            P(Bullet, R("Cut p95 latency of the routing service by 35% by adding Redis caching and query tuning.")),
            P(Bullet, R("Mentored four engineers and ran weekly design reviews for the platform group.")),
            TitleDate("Software Engineer, Brightmart Retail", "Jul 2017 – Feb 2021"),
            P(Bullet, R("Built order and inventory services with PostgreSQL and RabbitMQ for 200 stores.")),
            P(Bullet, R("Introduced contract tests that reduced failed releases across three teams.")),
            P(Style("Heading1"), R("Skills")),
            P("", R("Languages: ", B), R("Java, SQL, Kotlin, Python")),
            P("", R("Frameworks: ", B), R("Spring Boot, Hibernate, JUnit")),
            P("", R("Tools: ", B), R("Docker, Jenkins, Git, Grafana")),
            P(Style("Heading1"), R("Education")),
            P("", R("B.E. Computer Engineering, Pune Institute of Technology, 2017"))),
            "<w:headerReference w:type=\"default\" r:id=\"rIdHeader1\"/>" + SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Header = P(Style("Header"), R("Rohan Mehta — Curriculum Vitae")),
        Pages = 2,
    }.Build();

    private static byte[] TwoColumn() => new Package
    {
        Body = Body(string.Concat(
            P("<w:pStyle w:val=\"Title\"/><w:jc w:val=\"center\"/>", R("Kavya Iyer")),
            P("<w:jc w:val=\"center\"/>", R("Chennai · kavya.iyer@example.test · +91 90000 22222")),
            // continuous section break: the heading area above is one column
            P("<w:spacing w:after=\"0\"/><w:sectPr w:rsidR=\"00A12B34\"><w:type w:val=\"continuous\"/>" + SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/></w:sectPr>"),
            P("<w:pStyle w:val=\"Heading1\"/><w:keepNext/>", R("Summary")),
            P("<w:spacing w:after=\"120\"/>", R("Frontend engineer with six years of experience shipping React and TypeScript applications for fintech and healthcare products.")),
            P("<w:pStyle w:val=\"Heading1\"/><w:keepNext/>", R("Work Experience")),
            P("", R("Lead Frontend Engineer, Medisure Health", B)),
            P("", R("Jan 2022 – Present", I)),
            P(Bullet, R("Led the rebuild of the patient portal in React and TypeScript for 1.2 million users.")),
            P(Bullet, R("Set up Storybook and visual tests that the design team now uses for every component.")),
            P("", R("Frontend Engineer, Paywise Fintech", B)),
            P("", R("Jun 2018 – Dec 2021", I)),
            P(Bullet, R("Built the merchant dashboard with React, Redux and GraphQL.")),
            P(Bullet, R("Improved Lighthouse performance scores from 54 to 91 on the checkout flow.")),
            P("<w:pStyle w:val=\"Heading1\"/><w:keepNext/>", R("Skills")),
            P("", R("Core: ", B), R("React, TypeScript, JavaScript, GraphQL, HTML, CSS")),
            P("", R("Testing: ", B), R("Jest, Playwright, Storybook")),
            P("<w:pStyle w:val=\"Heading1\"/><w:keepNext/>", R("Education")),
            P("", R("B.Tech Information Technology, Anna Institute, 2018"))),
            "<w:type w:val=\"continuous\"/>" + SectA4 + "<w:cols w:num=\"2\" w:space=\"567\"/><w:docGrid w:linePitch=\"360\"/>"),
        Pages = 1,
    }.Build();

    private static string Cell(int width, params string[] paragraphs) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/></w:tcPr>{string.Concat(paragraphs)}</w:tc>";

    private static string Table(int[] widths, params string[] rows) =>
        "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/>" +
        "<w:tblBorders><w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/><w:insideH w:val=\"nil\"/><w:insideV w:val=\"nil\"/></w:tblBorders>" +
        "<w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"1\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr>" +
        $"<w:tblGrid>{string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>" +
        string.Concat(rows.Select(r => $"<w:tr w:rsidR=\"00A12B34\">{r}</w:tr>")) + "</w:tbl>";

    private static byte[] SidebarTable() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Arjun Nair")),
            Table([3000, 6638], Cell(3000,
                    P(Style("Heading2"), R("Contact")),
                    P("", R("arjun.nair@example.test")),
                    P("", R("+91 97000 33333")),
                    P("", R("Kochi, India")),
                    P(Style("Heading2"), R("Skills")),
                    P("", R("Cloud: ", B), R("AWS, Terraform, Kubernetes")),
                    P("", R("Languages: ", B), R("Go, Python, Bash")),
                    P("", R("Observability: ", B), R("Prometheus, Grafana, Loki"))),
                Cell(6638,
                    P(Style("Heading2"), R("Profile")),
                    P("", R("Site reliability engineer with eight years of experience running large Kubernetes platforms on AWS for media and gaming companies.")),
                    P(Style("Heading2"), R("Experience")),
                    P("", R("Staff SRE, Streamwave Media", B)),
                    P("", R("2020 – Present", I)),
                    P(Bullet, R("Ran 30 Kubernetes clusters on AWS with Terraform and Argo CD across three regions.")),
                    P(Bullet, R("Brought incident response time down by 45% with SLO-based alerting in Prometheus.")),
                    P("", R("DevOps Engineer, Pixelforge Games", B)),
                    P("", R("2016 – 2020", I)),
                    P(Bullet, R("Moved game backends from virtual machines to containers with zero downtime.")),
                    P(Bullet, R("Wrote Go tooling that automated capacity planning for launch weekends.")))),
            P(""),
            P(Style("Heading2"), R("Education")),
            P("", R("B.Tech Electronics, Kerala Technical University, 2016"))),
            SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Pages = 1,
    }.Build();

    private static byte[] TypedBullets() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Sneha Kulkarni")),
            P("", R("Bengaluru · sneha.k@example.test · +91 99000 44444")),
            P(Style("Heading1"), R("Summary")),
            P("", R("Data engineer with five years of experience building "), R("batch and streaming pipelines", B), R(" on Spark and Airflow for e-commerce analytics.")),
            P(Style("Heading1"), R("Experience")),
            P("", R("Data Engineer — Cartnest", B)),
            P("", R("Aug 2021 – Present", I)),
            P("<w:ind w:left=\"360\" w:hanging=\"360\"/>", R("• "), R("Pipelines: ", B), R("built 25 Airflow pipelines that load clickstream data into Snowflake every hour.")),
            P("<w:ind w:left=\"360\" w:hanging=\"360\"/>", R("• "), R("Quality: ", B), R("added data quality checks with Great Expectations that catch broken feeds before dashboards refresh.")),
            P("<w:ind w:left=\"360\" w:hanging=\"360\"/>", R("• Partnered with analysts to model orders and returns for the finance team.")),
            P("", R("Junior Data Engineer — Insightly Labs", B)),
            P("", R("Jun 2019 – Jul 2021", I)),
            P("<w:ind w:left=\"360\" w:hanging=\"360\"/>", R("– Wrote PySpark jobs that processed 2 TB of logs each night.")),
            P("<w:ind w:left=\"360\" w:hanging=\"360\"/>", R("– Maintained Kafka consumers for the recommendations team "), R("with clear runbooks", I), R(".")),
            P(Style("Heading1"), R("Skills")),
            P("", R("Data: ", B), R("Spark, PySpark, Airflow, Kafka, Snowflake, dbt")),
            P("", R("Languages: ", B), R("Python, SQL, Scala")),
            P(Style("Heading1"), R("Education")),
            P("", R("M.Sc. Statistics, Savitribai University, 2019"))),
            SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Pages = 1,
    }.Build();

    private static byte[] ThemeFonts()
    {
        const string theme = "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\" w:cstheme=\"minorBidi\"/>";
        const string heading = "<w:rFonts w:ascii=\"Georgia\" w:hAnsi=\"Georgia\"/><w:b/><w:smallCaps/><w:color w:val=\"1F4E79\"/><w:spacing w:val=\"10\"/><w:sz w:val=\"26\"/>";
        return new Package
        {
            Body = Body(string.Concat(
                P("<w:jc w:val=\"center\"/>", R("Vikram Singh", "<w:rFonts w:ascii=\"Georgia\" w:hAnsi=\"Georgia\"/><w:b/><w:color w:val=\"1F4E79\"/><w:sz w:val=\"40\"/>")),
                P("<w:jc w:val=\"center\"/>", R("Jaipur · vikram.singh@example.test · +91 96000 55555", theme)),
                P("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"1F4E79\"/></w:pBdr><w:spacing w:before=\"240\"/>", R("Profile", heading)),
                P("<w:spacing w:after=\"80\"/>",
                    R("Full-stack developer with six years of experience in ", theme), Spell(), R("Djangoo", theme), Spell("spellEnd"),
                    R(" and React, building ", theme, "00F1A2B3"), R("booking platforms for travel companies.", theme)),
                P("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"1F4E79\"/></w:pBdr><w:spacing w:before=\"240\"/>", R("Experience", heading)),
                P("", R("Senior Developer, Tripkart Travel", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:b/>")),
                P("", R("Apr 2020 – Present", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:i/><w:color w:val=\"595959\"/>")),
                P(Bullet, R("Built the hotel ", theme), R("search service", theme, "00C3D4E5"), R(" in Django and PostgreSQL with full-text ranking.", theme)),
                P(Bullet, R("Shipped a React booking flow that raised completed bookings by 18%.", theme)),
                P(Bullet, R("Ran ", theme), Spell(), R("blamless", theme), Spell("spellEnd"), R(" postmortems and kept the on-call rota humane.", theme)),
                P("", R("Developer, Holidayhub", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:b/>")),
                P("", R("Jan 2018 – Mar 2020", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:i/><w:color w:val=\"595959\"/>")),
                P(Bullet, R("Integrated payment gateways and wrote the refund reconciliation job.", theme)),
                P("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"1F4E79\"/></w:pBdr><w:spacing w:before=\"240\"/>", R("Skills", heading)),
                P("", R("Stack: ", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:b/>"), R("Python, Django, React, PostgreSQL, Celery", theme)),
                P("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"1F4E79\"/></w:pBdr><w:spacing w:before=\"240\"/>", R("Education", heading)),
                P("", R("B.Tech Computer Science, Rajasthan Engineering College, 2017", theme))),
                SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
            Pages = 1,
        }.Build();
    }

    private static string Link(string relId, string text) =>
        $"<w:hyperlink r:id=\"{relId}\" w:history=\"1\"><w:r><w:rPr><w:rStyle w:val=\"Hyperlink\"/></w:rPr><w:t>{X(text)}</w:t></w:r></w:hyperlink>";

    private static byte[] Hyperlinks() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Meera Pillai")),
            P("", R("Hyderabad · "), Link("rIdLinkedIn", "linkedin.com/in/meera-pillai-example"), R(" · "), Link("rIdGitHub", "github.com/meera-example")),
            P(Style("Heading1"), R("Summary")),
            P("", R("Mobile engineer with five years of experience building Android apps in Kotlin and Jetpack Compose for banking customers.")),
            P(Style("Heading1"), R("Experience")),
            P("", R("Android Engineer, Kosh Bank", B)),
            P("", R("2021 – Present", I)),
            P(Bullet, R("Rebuilt the account screens in Jetpack Compose and cut crash rates by half.")),
            P(Bullet, R("Added biometric login with the Android Keystore for 3 million users.")),
            P("", R("Mobile Developer, Quickpay", B)),
            P("", R("2019 – 2021", I)),
            P(Bullet, R("Wrote offline-first sync for the merchant app using Room and WorkManager.")),
            P(Style("Heading1"), R("Projects")),
            P(Bullet, R("Open-source Compose charts library: "), Link("rIdProject", "github.com/meera-example/charts")),
            P(Bullet, R("Wrote a Kotlin coroutines guide used in internal onboarding for new mobile engineers.")),
            P(Style("Heading1"), R("Skills")),
            P("", R("Mobile: ", B), R("Kotlin, Jetpack Compose, Android SDK, Room, Coroutines")),
            P(Style("Heading1"), R("Education")),
            P("", R("B.Tech Computer Science, Telangana Institute of Technology, 2019"))),
            "<w:headerReference w:type=\"default\" r:id=\"rIdHeader1\"/><w:footerReference w:type=\"default\" r:id=\"rIdFooter1\"/>" + SectA4 +
            "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Header = P(Style("Header"), R("meera.pillai@example.test · +91 95000 66666")),
        Footer = P("<w:pStyle w:val=\"Footer\"/><w:jc w:val=\"center\"/>", R("Page "),
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE   \\* MERGEFORMAT </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:rPr><w:noProof/></w:rPr><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"),
        Links = [("rIdLinkedIn", "https://www.linkedin.com/in/meera-pillai-example"), ("rIdGitHub", "https://github.com/meera-example"),
            ("rIdProject", "https://github.com/meera-example/charts")],
        Pages = 1,
    }.Build();

    private static string TextBox(string paragraphs) =>
        "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>" +
        "<wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"251659264\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>4300000</wp:posOffset></wp:positionH>" +
        "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV><wp:extent cx=\"1800000\" cy=\"2400000\"/>" +
        "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapSquare wrapText=\"bothSides\"/><wp:docPr id=\"2\" name=\"Text Box 2\"/><wp:cNvGraphicFramePr/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/>" +
        "<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1800000\" cy=\"2400000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr>" +
        $"<wps:txbx><w:txbxContent>{paragraphs}</w:txbxContent></wps:txbx>" +
        "<wps:bodyPr rot=\"0\" vert=\"horz\" wrap=\"square\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" anchor=\"t\" anchorCtr=\"0\"><a:noAutofit/></wps:bodyPr>" +
        "</wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice><mc:Fallback><w:pict>" +
        "<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>" +
        "<v:shape id=\"Text Box 2\" o:spid=\"_x0000_s1026\" type=\"#_x0000_t202\" style=\"position:absolute;margin-left:338.6pt;margin-top:0;width:141.75pt;height:189pt;z-index:251659264\">" +
        $"<v:textbox><w:txbxContent>{paragraphs}</w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>";

    private static byte[] TextBoxPhoto()
    {
        var box = P("", R("Skills", B)) + P("", R("C#, .NET, Azure")) + P("", R("SQL Server, Redis"));
        return new Package
        {
            Body = Body(string.Concat(
                P(Style("Title"), R("Farhan Qureshi")),
                P("", R("Lucknow · farhan.q@example.test · +91 94000 77777"), TextBox(box)),
                P(Style("Heading1"), R("Summary")),
                P("", R(".NET engineer with nine years of experience building insurance and claims systems on Azure, now leading a small backend team.")),
                P(Style("Heading1"), R("Experience")),
                P("", R("Engineering Lead, Suraksha Insurance", B)),
                P("", R("2019 – Present", I)),
                P(Bullet, R("Led five engineers building the claims platform in C# and ASP.NET Core on Azure.")),
                P(Bullet, R("Moved nightly batch jobs to Azure Functions and saved 22% on compute.")),
                P("", R("Senior Developer, Policybazaar Example", B)),
                P("", R("2015 – 2019", I)),
                P(Bullet, R("Built premium calculation services with SQL Server and Redis caching.")),
                P(Bullet, R("Wrote integration tests for every partner insurer feed.")),
                P(Style("Heading1"), R("Education")),
                P("", R("MCA, Lucknow Institute of Computer Applications, 2015"))),
                "<w:headerReference w:type=\"default\" r:id=\"rIdHeader1\"/>" + SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
            Header = P(Style("Header") + "<w:jc w:val=\"right\"/>",
                "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"540000\" cy=\"540000\"/>" +
                "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:docPr id=\"1\" name=\"Photo 1\" descr=\"Profile photo\"/>" +
                "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
                "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
                "<pic:nvPicPr><pic:cNvPr id=\"1\" name=\"photo.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                "<pic:blipFill><a:blip r:embed=\"rIdImage1\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"540000\" cy=\"540000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"),
            HeaderImage = true,
            Pages = 1,
        }.Build();
    }

    private static byte[] SkillsGrid()
    {
        string SkillCell(string label, string items) => Cell(4819, P("", R(label, B), R(items)));
        return new Package
        {
            Body = Body(string.Concat(
                P(Style("Title"), R("Ananya Rao")),
                P("", R("Mysuru · ananya.rao@example.test · +91 93000 88888")),
                P(Style("Heading1"), R("Profile")),
                P("", R("QA automation engineer with six years of experience testing payment and lending products with Selenium, Playwright and REST API suites.")),
                P(Style("Heading1"), R("Technical Skills")),
                Table([4819, 4819],
                    SkillCell("Automation: ", "Selenium, Playwright, Cypress") + SkillCell("Languages: ", "Java, TypeScript, Python"),
                    SkillCell("API testing: ", "Postman, REST Assured") + SkillCell("CI: ", "GitHub Actions, Jenkins")),
                P(Style("Heading1"), R("Experience")),
                P("", R("Senior QA Engineer, Lendfast", B)),
                P("", R("2021 – Present", I)),
                P(Bullet, R("Built a Playwright suite of 600 end-to-end tests that runs on every pull request.")),
                P(Bullet, R("Cut regression time from two days to three hours with parallel runs.")),
                P("", R("QA Engineer, Paystream", B)),
                P("", R("2018 – 2021", I)),
                P(Bullet, R("Automated card payment checks with Selenium and REST Assured.")),
                P(Style("Heading1"), R("Certifications")),
                P(Bullet, R("ISTQB Certified Tester Foundation Level")),
                P(Bullet, R("AWS Certified Cloud Practitioner")),
                P(Style("Heading1"), R("Education")),
                P("", R("B.E. Computer Science, Mysuru College of Engineering, 2018"))),
                SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
            Pages = 1,
        }.Build();
    }

    private static byte[] MultiLevel() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Deepak Verma")),
            P("", R("Noida · deepak.verma@example.test · +91 92000 99999")),
            P(Style("Heading1"), R("Summary")),
            P("", R("Platform engineer with ten years of experience in distributed systems, now focused on developer platforms and internal tooling.")),
            P(Style("Heading1"), R("Experience")),
            P("", R("Principal Engineer, Cloudnine Systems", B)),
            P("", R("2019 – Present", I)),
            P(Style("ListParagraph") + Num(1), R("Own the internal developer platform used by 300 engineers.")),
            P(Style("ListParagraph") + Num(1, 1), R("Designed the service template that creates a repository, pipeline and dashboards in minutes.")),
            P(Style("ListParagraph") + Num(1, 1), R("Built golden paths for Go and Java services with shared libraries.")),
            P(Style("ListParagraph") + Num(1), R("Chaired the architecture review board and wrote decision records for major changes.")),
            P("", R("Senior Engineer, Datafabric", B)),
            P("", R("2014 – 2019", I)),
            P(Style("ListParagraph") + Num(1), R("Built a distributed job scheduler in Go that handles 2 million jobs a day.")),
            P(Style("Heading1"), R("Key Projects")),
            P(Style("ListParagraph") + Num(2), R("Migrated the build system to Bazel and cut CI time by 60%.")),
            P(Style("ListParagraph") + Num(2), R("Introduced service ownership records so every alert reaches the right team.")),
            P(Style("Heading1"), R("Skills")),
            P("", R("Languages: ", B), R("Go, Java, Python")),
            P("", R("Platform: ", B), R("Kubernetes, Bazel, Backstage, Terraform")),
            P(Style("Heading1"), R("Education")),
            P("", R("M.Tech Computer Science, Delhi Institute of Technology, 2014"))),
            SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Pages = 2,
    }.Build();

    private static string Sdt(string alias, int id, string content) =>
        $"<w:sdt><w:sdtPr><w:alias w:val=\"{alias}\"/><w:tag w:val=\"{alias.ToLowerInvariant()}\"/><w:id w:val=\"{id}\"/></w:sdtPr><w:sdtContent>{content}</w:sdtContent></w:sdt>";

    private static byte[] ContentControls()
    {
        const string body = "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"21\"/>";
        return new Package
        {
            Body = Body(string.Concat(
                Sdt("Name", -1983041765, P(Style("Title"), R("Priya Desai"))),
                Sdt("Contact", 1094129781, P("", R("Ahmedabad · priya.desai@example.test · +91 91000 12121", body))),
                P(Style("Heading1"), R("Summary")),
                Sdt("Summary", 512347890, P("", R("Product-minded backend engineer with seven years of experience in Node.js and TypeScript building marketplaces and payments.", body))),
                P("", R("Experienced in Node.js microservices, event-driven design and pragmatic testing across small, fast-moving teams.", body)),
                P(Style("Heading1"), R("Experience")),
                P("", R("Senior Backend Engineer, Bazaarline", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:b/><w:sz w:val=\"22\"/>")),
                P("", R("2020 – Present", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:i/><w:sz w:val=\"20\"/>")),
                P(Bullet, R("Built the seller payouts service in Node.js and TypeScript with PostgreSQL.", body)),
                P(Bullet, R("Introduced event sourcing for orders and kept audit trails for every change.", body)),
                P("", R("Backend Engineer, Shopwise", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:b/><w:sz w:val=\"22\"/>")),
                P("", R("2017 – 2020", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:i/><w:sz w:val=\"20\"/>")),
                P(Bullet, R("Wrote the catalogue search API with Elasticsearch and Redis.", body)),
                P(Style("Heading1"), R("Skills")),
                P("", R("Backend: ", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:b/><w:sz w:val=\"21\"/>"), R("Node.js, TypeScript, PostgreSQL, Redis, Elasticsearch", body)),
                P(Style("Heading1"), R("Education")),
                P("", R("B.Tech Computer Engineering, Gujarat Technological Institute, 2017", body))),
                SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
            Pages = 1,
        }.Build();
    }

    private static byte[] UnicodeSpecial() => new Package
    {
        Body = Body(string.Concat(
            P(Style("Title"), R("Aditya Sharma")),
            P("", R("आदित्य शर्मा · Indore · aditya.s@example.test · +91 90123 45678")),
            P(Style("Heading1"), R("Summary")),
            P("", R("“Ship small, ship often” — backend engineer with 4+ years in Python & Go, building ₹-denominated payment rails for co-operative banks.")),
            P(Style("Heading1"), R("Experience")),
            P("", R("Backend Engineer – Sahakari Payments", B)),
            P("", R("2022 – Present", I)),
            P(Bullet, "<w:r><w:t xml:space=\"preserve\">Built UPI collect flows for 120 co</w:t><w:noBreakHyphen/><w:t>operative banks in Go.</w:t></w:r>"),
            P(Bullet, R("Reconciled ₹ 40 crore of daily settlements with zero manual entries <and> clear audit logs.")),
            P(Bullet, R("Worked with the team in "), "<w:smartTag w:uri=\"urn:schemas-microsoft-com:office:smarttags\" w:element=\"place\"><w:r><w:t>Indore</w:t></w:r></w:smartTag>",
                R(" to design idempotent payment retries and clear failure states.")),
            P("", R("Software Engineer – Digikhata", B)),
            P("", R("2020 – 2022", I)),
            P(Bullet, "<w:r><w:t xml:space=\"preserve\">Wrote Django services for the ledger’s multi</w:t><w:softHyphen/><w:t xml:space=\"preserve\">currency support.</w:t></w:r>"),
            P(Bullet, R("  Kept the test suite under ten minutes while it grew to 3,500 tests.  ")),
            P(Style("Heading1"), R("Skills")),
            P("", R("Languages: ", B), R("Python, Go, SQL")),
            P(Style("Heading1"), R("Education")),
            P("", R("B.E. Computer Science, Devi Ahilya University, 2020"))),
            SectA4 + "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/>"),
        Pages = 1,
    }.Build();

    private static byte[] GoogleDocsSections()
    {
        const string arial = "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"Arial\" w:cs=\"Arial\"/>";
        string Text(string t, bool bold = false, int halfPoints = 22) =>
            Rg(t, arial + (bold ? "<w:b/>" : "") + $"<w:sz w:val=\"{halfPoints}\"/><w:szCs w:val=\"{halfPoints}\"/>");
        const string spacing = "<w:spacing w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/>";
        string Item(string t, string extra = "") => Pg("<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr>" + spacing +
            "<w:ind w:left=\"720\" w:hanging=\"360\"/>" + extra, Text(t));
        return new Package
        {
            Body = Body(string.Concat(
                Pg(spacing, Text("Ishaan Gupta", bold: true, halfPoints: 36)),
                Pg(spacing, Text("Gurugram · ishaan.gupta@example.test · +91 98111 22334")),
                Pg("<w:spacing w:before=\"240\" w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/>", Text("SUMMARY", bold: true, halfPoints: 24)),
                Pg(spacing, "<w:bookmarkStart w:id=\"0\" w:name=\"_GoBack\"/><w:bookmarkEnd w:id=\"0\"/>",
                    Text("Machine learning engineer with five years of experience shipping ranking and recommendation models to production.")),
                Pg("<w:spacing w:before=\"240\" w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/>", Text("EXPERIENCE", bold: true, halfPoints: 24)),
                Pg(spacing, Text("ML Engineer, Feedly India", bold: true)),
                Pg(spacing, Text("2022 – Present")),
                Item("Trained ranking models in PyTorch that lifted click-through by 12% on the home feed."),
                Item("Built feature pipelines with Spark and served models behind a gRPC service."),
                // a section break inside a bullet paragraph: the edit must keep it
                Item("Set up offline evaluation so every model change ships with a comparison report.",
                    "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/><w:pgNumType w:start=\"1\"/><w:cols w:space=\"720\"/></w:sectPr>"),
                Pg(spacing, "<w:r><w:br w:type=\"page\"/></w:r>"),
                Pg(spacing, Text("Data Scientist, Shopkart", bold: true)),
                Pg(spacing, Text("2019 – 2022")),
                Pg("<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr>" + spacing + "<w:ind w:left=\"720\" w:hanging=\"360\"/>",
                    $"<w:r><w:rPr>{arial}<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr><w:lastRenderedPageBreak/><w:t xml:space=\"preserve\">Built demand forecasting models in Python that the supply team used every week.</w:t></w:r>"),
                Item("Ran A/B tests for search ranking and wrote the analysis guide for the team."),
                Pg("<w:spacing w:before=\"240\" w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/>", Text("SKILLS", bold: true, halfPoints: 24)),
                Pg(spacing, Text("ML: ", bold: true), Text("PyTorch, scikit-learn, Spark, Airflow")),
                Pg("<w:spacing w:before=\"240\" w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/>", Text("EDUCATION", bold: true, halfPoints: 24)),
                Pg(spacing, Text("M.Tech Data Science, Haryana Institute of Technology, 2019"))),
                "<w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/><w:pgNumType w:start=\"1\"/><w:cols w:space=\"720\"/>"),
            GoogleStyle = true,
            Pages = 2,
        }.Build();
    }

    // =====================================================================================================================
    // Package assembly (the parts Word writes)
    // =====================================================================================================================

    private sealed class Package
    {
        public required string Body { get; init; }
        public string? Header { get; init; }
        public string? Footer { get; init; }
        public bool HeaderImage { get; init; }
        public (string Id, string Url)[] Links { get; init; } = [];
        public int Pages { get; init; } = 1;
        public bool GoogleStyle { get; init; }

        private const string Ns =
            "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" " +
            "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
            "xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:w10=\"urn:schemas-microsoft-com:office:word\" " +
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" " +
            "xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\" xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
            "xmlns:wpi=\"http://schemas.microsoft.com/office/word/2010/wordprocessingInk\" xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" mc:Ignorable=\"w14 w15 wp14\"";

        private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";

        public byte[] Build()
        {
            var parts = new List<(string Name, byte[] Data)>();
            void Add(string name, string xml) => parts.Add((name, Encoding.UTF8.GetBytes(Decl + xml)));

            var overrides = new StringBuilder()
                .Append("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>")
                .Append("<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>")
                .Append("<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>")
                .Append("<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>")
                .Append("<Override PartName=\"/word/webSettings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.webSettings+xml\"/>")
                .Append("<Override PartName=\"/word/fontTable.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml\"/>")
                .Append("<Override PartName=\"/word/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>")
                .Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>")
                .Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            if (Header is not null)
                overrides.Append("<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>");
            if (Footer is not null)
                overrides.Append("<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>");
            Add("[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" + overrides + "</Types>");
            Add("_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
            Add("docProps/app.xml",
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                $"<Template>Normal.dotm</Template><TotalTime>42</TotalTime><Pages>{Pages}</Pages><Words>420</Words><Characters>2400</Characters>" +
                "<Application>Microsoft Office Word</Application><DocSecurity>0</DocSecurity><Lines>20</Lines><Paragraphs>6</Paragraphs>" +
                "<ScaleCrop>false</ScaleCrop><Company></Company><LinksUpToDate>false</LinksUpToDate><CharactersWithSpaces>2800</CharactersWithSpaces>" +
                "<SharedDoc>false</SharedDoc><HyperlinksChanged>false</HyperlinksChanged><AppVersion>16.0000</AppVersion></Properties>");
            Add("docProps/core.xml",
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                "<dc:title></dc:title><dc:creator>Example</dc:creator><cp:lastModifiedBy>Example</cp:lastModifiedBy><cp:revision>7</cp:revision>" +
                "<dcterms:created xsi:type=\"dcterms:W3CDTF\">2026-09-01T10:00:00Z</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">2026-10-01T10:00:00Z</dcterms:modified>" +
                "</cp:coreProperties>");

            var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">")
                .Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>")
                .Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>")
                .Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>")
                .Append("<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/webSettings\" Target=\"webSettings.xml\"/>")
                .Append("<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/fontTable\" Target=\"fontTable.xml\"/>")
                .Append("<Relationship Id=\"rId6\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
            if (Header is not null)
                rels.Append("<Relationship Id=\"rIdHeader1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>");
            if (Footer is not null)
                rels.Append("<Relationship Id=\"rIdFooter1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
            foreach (var (id, url) in Links)
                rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{X(url)}\" TargetMode=\"External\"/>");
            Add("word/_rels/document.xml.rels", rels.Append("</Relationships>").ToString());

            Add("word/document.xml", $"<w:document {Ns}>{Body}</w:document>");
            if (Header is not null)
            {
                Add("word/header1.xml", $"<w:hdr {Ns}>{Header}</w:hdr>");
                if (HeaderImage)
                {
                    Add("word/_rels/header1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rIdImage1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/></Relationships>");
                    parts.Add(("word/media/image1.png", OnePixelPng));
                }
            }
            if (Footer is not null) Add("word/footer1.xml", $"<w:ftr {Ns}>{Footer}</w:ftr>");
            Add("word/styles.xml", GoogleStyle ? GoogleStyles : WordStyles);
            Add("word/numbering.xml", Numbering);
            Add("word/settings.xml", Settings);
            Add("word/webSettings.xml",
                "<w:webSettings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:optimizeForBrowser/><w:allowPNG/></w:webSettings>");
            Add("word/fontTable.xml", FontTable);
            Add("word/theme/theme1.xml", Theme);

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // [Content_Types].xml first, as Word writes it.
                foreach (var (name, data) in parts.OrderBy(p => p.Name == "[Content_Types].xml" ? 0 : 1))
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var s = entry.Open();
                    s.Write(data);
                }
            }
            return ms.ToArray();
        }

        private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

        private static readonly string WordStyles =
            $"<w:styles {W}>" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:asciiTheme=\"minorHAnsi\" w:eastAsiaTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\" w:cstheme=\"minorBidi\"/>" +
            "<w:kern w:val=\"2\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-IN\" w:eastAsia=\"en-US\" w:bidi=\"ar-SA\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:link w:val=\"Heading1Char\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>" +
            "<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"0\"/></w:pPr>" +
            "<w:rPr><w:rFonts w:asciiTheme=\"majorHAnsi\" w:eastAsiaTheme=\"majorEastAsia\" w:hAnsiTheme=\"majorHAnsi\" w:cstheme=\"majorBidi\"/><w:b/><w:color w:val=\"2F5496\" w:themeColor=\"accent1\" w:themeShade=\"BF\"/><w:sz w:val=\"28\"/><w:szCs w:val=\"28\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:unhideWhenUsed/><w:qFormat/>" +
            "<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"160\" w:after=\"40\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"24\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"10\"/><w:qFormat/>" +
            "<w:pPr><w:spacing w:after=\"80\" w:line=\"240\" w:lineRule=\"auto\"/><w:contextualSpacing/></w:pPr><w:rPr><w:spacing w:val=\"-10\"/><w:kern w:val=\"28\"/><w:sz w:val=\"48\"/><w:szCs w:val=\"48\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"34\"/><w:qFormat/><w:pPr><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Header\"><w:name w:val=\"header\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:pPr><w:tabs><w:tab w:val=\"center\" w:pos=\"4513\"/><w:tab w:val=\"right\" w:pos=\"9026\"/></w:tabs><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Footer\"><w:name w:val=\"footer\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:pPr><w:tabs><w:tab w:val=\"center\" w:pos=\"4513\"/><w:tab w:val=\"right\" w:pos=\"9026\"/></w:tabs><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>" +
            "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
            "<w:style w:type=\"character\" w:customStyle=\"1\" w:styleId=\"Heading1Char\"><w:name w:val=\"Heading 1 Char\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:link w:val=\"Heading1\"/><w:uiPriority w:val=\"9\"/>" +
            "<w:rPr><w:rFonts w:asciiTheme=\"majorHAnsi\" w:hAnsiTheme=\"majorHAnsi\"/><w:b/><w:color w:val=\"2F5496\" w:themeColor=\"accent1\" w:themeShade=\"BF\"/><w:sz w:val=\"28\"/></w:rPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:rPr><w:color w:val=\"0563C1\" w:themeColor=\"hyperlink\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
            "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>" +
            "<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
            "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:uiPriority w:val=\"39\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
            "<w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/></w:tblBorders></w:tblPr></w:style>" +
            "<w:style w:type=\"numbering\" w:default=\"1\" w:styleId=\"NoList\"><w:name w:val=\"No List\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
            "</w:styles>";

        private static readonly string GoogleStyles =
            $"<w:styles {W}>" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-GB\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"normal\"/></w:style>" +
            "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"0\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
            "</w:styles>";

        private static readonly string Numbering =
            $"<w:numbering {W}>" +
            "<w:abstractNum w:abstractNumId=\"0\"><w:nsid w:val=\"1D2A5B44\"/><w:multiLevelType w:val=\"hybridMultilevel\"/><w:tmpl w:val=\"40090001\"/>" +
            "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Symbol\" w:hAnsi=\"Symbol\" w:hint=\"default\"/></w:rPr></w:lvl>" +
            "<w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"o\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"1440\" w:hanging=\"360\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\" w:hint=\"default\"/></w:rPr></w:lvl>" +
            "</w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"1\"><w:nsid w:val=\"5E3C7A10\"/><w:multiLevelType w:val=\"hybridMultilevel\"/><w:tmpl w:val=\"4009000F\"/>" +
            "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl>" +
            "</w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"2\"><w:nsid w:val=\"0A7F1C22\"/><w:multiLevelType w:val=\"multilevel\"/>" +
            "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"●\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr><w:rPr><w:u w:val=\"none\"/></w:rPr></w:lvl>" +
            "</w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>" +
            "<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num>" +
            "<w:num w:numId=\"3\"><w:abstractNumId w:val=\"2\"/></w:num>" +
            "</w:numbering>";

        private static readonly string Settings =
            $"<w:settings {W} xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\">" +
            "<w:zoom w:percent=\"100\"/><w:proofState w:spelling=\"clean\" w:grammar=\"clean\"/><w:defaultTabStop w:val=\"720\"/>" +
            "<w:characterSpacingControl w:val=\"doNotCompress\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>" +
            "<w:rsids><w:rsidRoot w:val=\"00A12B34\"/><w:rsid w:val=\"00A12B34\"/><w:rsid w:val=\"00C56D78\"/><w:rsid w:val=\"00E91F02\"/></w:rsids>" +
            "<m:mathPr><m:mathFont m:val=\"Cambria Math\"/></m:mathPr><w:themeFontLang w:val=\"en-IN\"/>" +
            "<w:decimalSymbol w:val=\".\"/><w:listSeparator w:val=\",\"/></w:settings>";

        private static readonly string FontTable =
            $"<w:fonts {W}>" +
            "<w:font w:name=\"Calibri\"><w:panose1 w:val=\"020F0502020204030204\"/><w:charset w:val=\"00\"/><w:family w:val=\"swiss\"/><w:pitch w:val=\"variable\"/></w:font>" +
            "<w:font w:name=\"Symbol\"><w:panose1 w:val=\"05050102010706020507\"/><w:charset w:val=\"02\"/><w:family w:val=\"roman\"/><w:pitch w:val=\"variable\"/></w:font>" +
            "<w:font w:name=\"Arial\"><w:panose1 w:val=\"020B0604020202020204\"/><w:charset w:val=\"00\"/><w:family w:val=\"swiss\"/><w:pitch w:val=\"variable\"/></w:font>" +
            "<w:font w:name=\"Georgia\"><w:panose1 w:val=\"02040502050405020303\"/><w:charset w:val=\"00\"/><w:family w:val=\"roman\"/><w:pitch w:val=\"variable\"/></w:font>" +
            "</w:fonts>";

        private static readonly string Theme =
            "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office Theme\"><a:themeElements>" +
            "<a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1>" +
            "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
            "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>" +
            "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
            "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
            "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";

        private static readonly byte[] OnePixelPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    }
}
