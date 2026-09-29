using System.Reflection;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using ToroSquad.Core.Localization;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Commands;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Tests.Unit;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// The 🔮 Öngörü Oluştur form as Discord receives it (Discord.Net 3.20 builders and serializer; at most five labelled
/// components, label ≤ 45, description and placeholder ≤ 100): five fields — title, outcomes, lock date, lock time, result
/// rule — with short end-user hints (limits and formats appear only in validation messages), a real multi-line outcomes
/// example, and every typed value back in its field when the form is reopened with ✏️ Düzenle.
/// </summary>
public sealed class PredictionModalContractTests
{
    private static readonly LocalizationCatalog Catalog = (LocalizationCatalog)PredictionDomainTests.Localizer();

    private static Func<string, string> L(string language) => key => Catalog.Get(language, key);

    private static readonly PredictionFormValues Typed = new("Galatasaray - Fenerbahçe maç sonucu ne olur?", "Galatasaray Kazanır | 1.10\nBerabere | 2.30",
        "05.10.2026", "20:00", "Normal süre sonucu geçerlidir.");

    private static List<LabelComponent> Labels(Modal modal) => modal.Component.Components.Select(c => c.Should().BeOfType<LabelComponent>().Subject).ToList();

    private static TextInputComponent Input(Modal modal, string id) =>
        Labels(modal).Select(l => l.Component).OfType<TextInputComponent>().Single(t => t.CustomId == id);

    [Fact]
    public void The_create_form_has_exactly_title_outcomes_lock_date_lock_time_and_result_rule_in_turkish()
    {
        var modal = PredictionFormUi.CreateModal("draft", PredictionFormValues.Empty, "2.00", L("tr"));

        modal.Title.Should().Be("🔮 Öngörü Oluştur");
        var labels = Labels(modal);
        labels.Select(l => (l.Label, l.Description, ((TextInputComponent)l.Component).CustomId, ((TextInputComponent)l.Component).Required)).Should().Equal(
            ("Başlık", "Öngörünün sorusunu kısa ve net yaz.", PredictionFormUi.TitleField, true),
            ("Seçenekler ve oranlar", "Her seçeneği yeni satıra yaz. Oran eklemek için | kullan. Oran yazmazsan 2.00 kullanılır.", PredictionFormUi.OutcomesField, true),
            ("Kilitlenme tarihi", "Boş bırakırsan öngörü manuel olarak kilitlenir.", PredictionFormUi.LockDateField, false),
            ("Kilitlenme saati", "Türkiye saati.", PredictionFormUi.LockTimeField, false),
            ("Sonuç kuralı", "Sonucun nasıl belirleneceğini gerekiyorsa belirt.", PredictionFormUi.RulesField, false));
        Input(modal, PredictionFormUi.TitleField).Placeholder.Should().Be("Galatasaray - Fenerbahçe maç sonucu ne olur?");
        Input(modal, PredictionFormUi.LockDateField).Placeholder.Should().Be("05.10.2026");
        Input(modal, PredictionFormUi.LockTimeField).Placeholder.Should().Be("20:00");
        Input(modal, PredictionFormUi.RulesField).Placeholder.Should().Be("Normal süre sonucu geçerlidir; uzatmalar dahil değildir.");
        Input(modal, PredictionFormUi.RulesField).Style.Should().Be(TextInputStyle.Paragraph);
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void The_old_combined_lock_field_is_gone_and_no_hint_explains_limits_or_parsing(string language)
    {
        var modal = PredictionFormUi.CreateModal("draft", PredictionFormValues.Empty, "2.00", L(language));
        var labels = Labels(modal);

        labels.Should().HaveCount(5, "Discord allows at most five components in a modal");
        labels.Select(l => ((TextInputComponent)l.Component).CustomId).Should().NotContain("lock").And.OnlyHaveUniqueItems();
        typeof(PredictionFormUi).GetField("LockField").Should().BeNull();
        typeof(PredictionFormModal).GetProperty("LockAt").Should().BeNull();
        foreach (var label in labels)
        {
            label.Label.Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxTitleLength);
            label.Description.Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxHintLength);
            ((TextInputComponent)label.Component).Placeholder.Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxHintLength);
            label.Description.Should().NotMatchRegex(@"karakter|character|2–25|1,10|GG\.AA|SS:DD|DD\.MM|HH:MM|parser|en büyük yazı", label.Label);
        }
    }

    [Fact]
    public void The_outcomes_example_is_a_real_three_line_paragraph_placeholder_on_the_wire()
    {
        var modal = PredictionFormUi.CreateModal("draft", PredictionFormValues.Empty, "2.00", L("tr"));
        var outcomes = Input(modal, PredictionFormUi.OutcomesField);

        outcomes.Style.Should().Be(TextInputStyle.Paragraph);
        outcomes.Placeholder.Should().Be("Galatasaray Kazanır | 1.10\nBerabere | 2.30\nFenerbahçe Kazanır | 3.10");
        var wire = Wire(modal.Component.Components).EnumerateArray().Select(l => l.GetProperty("component")).ToList();
        wire[1].GetProperty("placeholder").GetString()!.Split('\n').Should().HaveCount(3, "the example keeps its line breaks when sent to Discord");
        wire[1].GetProperty("style").GetInt32().Should().Be((int)TextInputStyle.Paragraph);
    }

    [Fact]
    public void The_hint_names_the_configured_default_odds()
    {
        var modal = PredictionFormUi.CreateModal("draft", PredictionFormValues.Empty, "1.50", L("tr"));
        Labels(modal)[1].Description.Should().EndWith("Oran yazmazsan 1.50 kullanılır.");
    }

    [Fact]
    public void Reopening_with_duzenle_puts_every_typed_value_back_including_date_and_time()
    {
        var modal = PredictionFormUi.CreateModal("draft", Typed, "2.00", L("tr"));

        modal.CustomId.Should().Be(PredictionMessages.FormModalPrefix + "draft");
        Input(modal, PredictionFormUi.TitleField).Value.Should().Be(Typed.Title);
        Input(modal, PredictionFormUi.OutcomesField).Value.Should().Be(Typed.Outcomes);
        Input(modal, PredictionFormUi.LockDateField).Value.Should().Be("05.10.2026");
        Input(modal, PredictionFormUi.LockTimeField).Value.Should().Be("20:00");
        Input(modal, PredictionFormUi.RulesField).Value.Should().Be(Typed.Rules);
        new PredictionFormModal { Question = Typed.Title, Outcomes = Typed.Outcomes, LockDate = "05.10.2026", LockTime = "20:00", Rules = Typed.Rules }
            .ToValues().Should().Be(Typed, "the submitted fields bind back to the same five values");
    }

    [Fact]
    public void The_submitted_fields_bind_by_the_same_custom_ids()
    {
        var bound = typeof(PredictionFormModal).GetProperties()
            .Select(p => p.GetCustomAttribute<ModalTextInputAttribute>()?.CustomId).OfType<string>().ToList();
        bound.Should().Equal(PredictionFormUi.TitleField, PredictionFormUi.OutcomesField, PredictionFormUi.LockDateField, PredictionFormUi.LockTimeField,
            PredictionFormUi.RulesField);
    }

    private static JsonElement Wire(IEnumerable<IMessageComponent> components)
    {
        var rest = typeof(global::Discord.Rest.DiscordRestClient);
        var toModel = rest.Assembly.GetType("Discord.Rest.MessageComponentExtension")!
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ToModel" && m.GetParameters() is [{ ParameterType: var type }] && type == typeof(IMessageComponent) && m.ReturnType == typeof(IMessageComponent));
        var serializer = (Newtonsoft.Json.JsonSerializer)rest.GetField("Serializer", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var writer = new StringWriter();
        serializer.Serialize(writer, components.Select(c => toModel.Invoke(null, [c])).ToArray());
        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }
}
