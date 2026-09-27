using System.Reflection;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Commands;
using ToroSquad.Modules.Lfg.Domain;
using GuildPermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// The listing form as Discord receives it (Discord.Net 3.20 builders, Discord's documented modal limits: at most five
/// top-level components, Label ≤ 45 / description ≤ 100, text input placeholder ≤ 100, custom ids ≤ 100; modals cannot hold
/// disabled components) and as the Interaction Framework reads it back: the same field ids, nothing but a random draft id in
/// any custom id, every text defused.
/// </summary>
public sealed class LfgModalContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly LocalizationCatalog Catalog = new([new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization")]);
    private static readonly string[] Fields = [LfgForm.GameField, LfgForm.PlayersField, LfgForm.DetailsField, LfgForm.StartField, LfgForm.DurationField];

    private static LfgFormUi.Text L(string language) => (key, args) => Catalog.Get(language, key, args);

    private static LfgFormDraft Draft(LfgFormKind kind = LfgFormKind.Create, LfgFormValues? values = null, ChannelId? voice = null, bool before = false) =>
        new LfgFormDrafts(new FakeTimeProvider(T0)).Open(new ActorContext(new GuildId(1), new UserId(10), GuildPermission.ViewChannel, [], false, 1),
            new ChannelId(2), kind, kind == LfgFormKind.Edit ? 7 : null, values ?? LfgFormValues.Empty, before, false, voice);

    private static List<(LabelComponent Label, TextInputComponent Input)> Inputs(Modal modal) =>
        modal.Component.Components.Select(c => c.Should().BeOfType<LabelComponent>().Subject)
            .Select(l => (l, l.Component.Should().BeOfType<TextInputComponent>().Subject)).ToList();

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void The_create_form_fits_discords_modal_limits(string language)
    {
        var draft = Draft();
        var modal = LfgFormUi.Modal(draft, 20, L(language));

        modal.Title.Length.Should().BeInRange(1, LfgFormUi.MaxTitleLength);
        modal.CustomId.Should().Be(LfgForm.ModalPrefix + draft.Id).And.HaveLength(LfgForm.ModalPrefix.Length + 22);
        modal.CustomId.Length.Should().BeLessThanOrEqualTo(ComponentBuilder.MaxCustomIdLength);
        var inputs = Inputs(modal);
        inputs.Should().HaveCount(LfgFormUi.MaxModalComponents);
        inputs.Select(i => i.Input.CustomId).Should().Equal(Fields);
        foreach (var (label, input) in inputs)
        {
            label.Label.Length.Should().BeInRange(1, LfgFormUi.MaxLabelLength, label.Label);
            label.Description!.Length.Should().BeInRange(1, LfgFormUi.MaxDescriptionLength, label.Description);
            input.Placeholder!.Length.Should().BeInRange(1, TextInputBuilder.MaxPlaceholderLength, input.Placeholder);
            input.Value.Should().BeNull("a new form is empty");
        }

        var byId = inputs.ToDictionary(i => i.Input.CustomId, i => i.Input);
        (byId["game"].MinLength, byId["game"].MaxLength, byId["game"].Required).Should().Be(((int?)LfgRules.GameNameMinLength, (int?)LfgRules.GameNameMaxLength, (bool?)true));
        (byId["players"].MaxLength, byId["players"].Required).Should().Be(((int?)LfgForm.PlayersMaxLength, (bool?)true));
        (byId["details"].MaxLength, byId["details"].Required, byId["details"].Style).Should().Be(((int?)LfgRules.DetailsMaxLength, (bool?)false, TextInputStyle.Paragraph));
        (byId["start"].MaxLength, byId["start"].Required).Should().Be(((int?)LfgForm.StartMaxLength, (bool?)false));
        (byId["duration"].MaxLength, byId["duration"].Required).Should().Be(((int?)LfgForm.DurationMaxLength, (bool?)false));
    }

    [Fact]
    public void The_create_form_explains_every_field_in_turkish()
    {
        var inputs = Inputs(LfgFormUi.Modal(Draft(), 20, L("tr")));

        LfgFormUi.Modal(Draft(), 20, L("tr")).Title.Should().Be("Ekip İlanı Oluştur");
        inputs.Select(i => i.Label.Label).Should().Equal("Oyun / Etkinlik", "Kişi sayısı", "Detay", "Başlangıç", "Süre (saat)");
        inputs[1].Label.Description.Should().Be("Sen dahil toplam ekip: 2–20");
        inputs[3].Label.Description.Should().Be("Boş = şimdi • 30 dk • 2 saat • 05.10.2026 21:30");
    }

    [Fact]
    public void The_edit_form_is_the_same_form_filled_with_the_listing()
    {
        var values = new LfgFormValues("Deadlock", "6", "Casual oynayacağız", "05.10.2026 21:30", "2");
        var modal = LfgFormUi.Modal(Draft(LfgFormKind.Edit, values), 20, L("tr"));

        modal.Title.Should().Be("Ekip İlanını Düzenle");
        Inputs(modal).Select(i => i.Input.Value).Should().Equal("Deadlock", "6", "Casual oynayacağız", "05.10.2026 21:30", "2");
        Inputs(modal).Select(i => i.Input.CustomId).Should().Equal(Fields, "create and edit share one form");
    }

    [Fact]
    public void The_bound_modal_reads_exactly_the_fields_the_form_sends()
    {
        var bound = typeof(LfgFormModal).GetProperties().Select(p => p.GetCustomAttribute<ModalTextInputAttribute>()?.CustomId).OfType<string>();

        bound.Should().BeEquivalentTo(Fields);
        new LfgFormModal { Game = "g", Players = "2", Details = "d", Start = "s", Duration = "1" }.ToValues().Should().Be(new LfgFormValues("g", "2", "d", "s", "1"));
    }

    [Fact]
    public void The_settings_step_uses_native_selects_and_carries_only_the_draft_id()
    {
        var draft = Draft(voice: new ChannelId(8802), before: true) with
        {
            Preview = new LfgFormPreview("Deadlock", "Casual", 6, LfgStart.After(TimeSpan.FromHours(2)), null, TimeSpan.FromHours(2)),
        };
        draft = draft with { Preview = draft.Preview! with { EventAt = T0.AddHours(2) } };

        var (content, components) = LfgFormUi.Settings(draft, T0, L("tr"));

        content.Should().Contain("🎮 **Deadlock** · 👥 6 kişi").And.Contain("🗓️ Başlangıç: 2 saat sonra").And.Contain("⏳ Süre: 2 saat").And.Contain("İlanı Oluştur");
        var rows = components.Components.Cast<ActionRowComponent>().ToList();
        rows.Should().HaveCountLessThanOrEqualTo(ComponentBuilder.MaxActionRowCount);
        var notify = rows[0].Components.Should().ContainSingle().Which.Should().BeOfType<SelectMenuComponent>().Subject;
        notify.CustomId.Should().Be(LfgFormUi.NotifyPrefix + draft.Id);
        (notify.MinValues, notify.MaxValues).Should().Be((0, 2));
        notify.Options.Select(o => (o.Value, o.IsDefault)).Should().Equal(("before", (bool?)true), ("start", (bool?)false));
        var voice = rows[1].Components.Should().ContainSingle().Which.Should().BeOfType<SelectMenuComponent>().Subject;
        voice.Type.Should().Be(ComponentType.ChannelSelect);
        voice.CustomId.Should().Be(LfgFormUi.VoicePrefix + draft.Id);
        voice.ChannelTypes.Should().Equal(ChannelType.Voice);
        (voice.MinValues, voice.MaxValues).Should().Be((0, 1));
        voice.DefaultValues.Should().ContainSingle().Which.Id.Should().Be(8802UL);
        rows[2].Components.Cast<ButtonComponent>().Select(b => b.CustomId).Should().Equal(
            LfgFormUi.SavePrefix + draft.Id, LfgFormUi.BackPrefix + draft.Id, LfgFormUi.CancelPrefix + draft.Id);
        rows.SelectMany(r => r.Components).OfType<IInteractableComponent>().Select(c => c.CustomId)
            .Should().OnlyContain(id => id.EndsWith(":" + draft.Id, StringComparison.Ordinal) && id.Length <= ComponentBuilder.MaxCustomIdLength);
    }

    [Fact]
    public void A_listing_that_starts_now_offers_no_notices()
    {
        var draft = Draft() with { Preview = new LfgFormPreview("CS2", null, 5, LfgStart.Now, null, TimeSpan.FromHours(2)) };

        var (content, components) = LfgFormUi.Settings(draft, T0, L("tr"));

        content.Should().Contain("🗓️ Başlangıç: Şimdi").And.Contain("yalnızca ileri bir başlangıç");
        components.Components.Cast<ActionRowComponent>().SelectMany(r => r.Components).OfType<SelectMenuComponent>()
            .Should().ContainSingle().Which.Type.Should().Be(ComponentType.ChannelSelect);
    }

    [Fact]
    public void The_settings_text_defuses_what_was_typed()
    {
        var draft = Draft(LfgFormKind.Edit) with
        {
            Preview = new LfgFormPreview("@everyone <@&1> Game", "<@123> **bold** https://evil.example", 5, LfgStart.AtInstant(T0.AddDays(1)), T0.AddDays(1),
                TimeSpan.FromMinutes(90)),
        };

        var (content, _) = LfgFormUi.Settings(draft, T0, L("tr"));

        DiscordText.RawMentionPattern().IsMatch(content).Should().BeFalse();
        content.Should().NotContain("https://").And.Contain("<t:" + T0.AddDays(1).ToUnixTimeSeconds() + ":F>").And.Contain("1 saat 30 dk").And.Contain("Kaydet");
    }

    [Fact]
    public void Form_custom_ids_never_shadow_the_card_buttons()
    {
        string[] prefixes =
        [
            LfgCardRenderer.JoinPrefix, LfgCardRenderer.MaybePrefix, LfgCardRenderer.LeavePrefix, LfgCardRenderer.VoicePrefix, LfgCardRenderer.ClosePrefix,
            LfgCardRenderer.EditPrefix, LfgForm.ModalPrefix, LfgFormUi.NotifyPrefix, LfgFormUi.VoicePrefix, LfgFormUi.SavePrefix, LfgFormUi.BackPrefix,
            LfgFormUi.CancelPrefix, LfgCommands.ConfirmClosePrefix, LfgCommands.KeepOpenPrefix,
        ];

        foreach (var a in prefixes)
            prefixes.Where(b => b != a).Should().NotContain(b => b.StartsWith(a, StringComparison.Ordinal), $"{a} would also match another handler");
    }

    [Fact]
    public void A_refused_form_offers_the_way_back_into_it()
    {
        var draft = Draft();

        var buttons = LfgFormUi.Retry(draft, L("tr")).Components.Cast<ActionRowComponent>().Single().Components.Cast<ButtonComponent>().ToList();

        buttons.Select(b => (b.Label, b.CustomId)).Should().Equal(("✏️ Formu Düzenle", LfgFormUi.BackPrefix + draft.Id), ("İptal", LfgFormUi.CancelPrefix + draft.Id));
    }
}
