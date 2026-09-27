using System.Reflection;
using System.Text.Json;
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
/// The listing form as Discord receives it (Discord.Net 3.20 builders and serializer; Discord's documented modal limits: at
/// most five top-level components, each in a Label ≤ 45 / description ≤ 100; text input placeholder ≤ 100; custom ids ≤ 100;
/// a string select holds ≤ 25 options; a checkbox group 1–10 options; modals cannot hold disabled components) and as it is
/// read back: the same field ids, nothing but a random draft id in any custom id, every text defused.
/// </summary>
public sealed class LfgModalContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly LocalizationCatalog Catalog = new([new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization")]);

    /// <summary>The production configuration (<c>Lfg:MaxPlayersPerListing</c>).</summary>
    private const int MaxPlayers = 20;

    private static LfgFormUi.Text L(string language) => (key, args) => Catalog.Get(language, key, args);

    private static LfgFormDraft Draft(LfgFormKind kind = LfgFormKind.Create, LfgFormValues? values = null, ChannelId? voice = null, bool before = false,
        bool atStart = false) =>
        new LfgFormDrafts(new FakeTimeProvider(T0)).Open(new ActorContext(new GuildId(1), new UserId(10), GuildPermission.ViewChannel, [], false, 1),
            new ChannelId(2), kind, kind == LfgFormKind.Edit ? 7 : null, values ?? LfgFormValues.Empty, before, atStart, voice);

    private static List<LabelComponent> Labels(Modal modal) => modal.Component.Components.Select(c => c.Should().BeOfType<LabelComponent>().Subject).ToList();

    private static TextInputComponent Input(Modal modal, string id) =>
        Labels(modal).Select(l => l.Component).OfType<TextInputComponent>().Single(t => t.CustomId == id);

    private static SelectMenuComponent Select(Modal modal, string id) =>
        Labels(modal).Select(l => l.Component).OfType<SelectMenuComponent>().Single(s => s.CustomId == id);

    private static CheckboxGroupComponent Notices(Modal modal) => Labels(modal).Select(l => l.Component).OfType<CheckboxGroupComponent>().Single();

    private static LfgFormDraft Checked(LfgFormDraft draft, DateTimeOffset? eventAt, TimeSpan? duration = null, string? details = "Casual") => draft with
    {
        Preview = new LfgFormPreview("Deadlock", details, 6, eventAt is { } at ? LfgStart.AtInstant(at) : LfgStart.Now, eventAt, duration ?? TimeSpan.FromHours(2)),
    };

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void The_main_form_holds_game_team_size_start_voice_and_notices(string language)
    {
        var draft = Draft();
        var modal = LfgFormUi.Modal(draft, MaxPlayers, L(language));

        modal.Title.Length.Should().BeInRange(1, LfgFormUi.MaxTitleLength);
        modal.CustomId.Should().Be(LfgForm.ModalPrefix + draft.Id).And.HaveLength(LfgForm.ModalPrefix.Length + 22);
        var labels = Labels(modal);
        labels.Select(l => l.Component.Type).Should().Equal(
            ComponentType.TextInput, ComponentType.SelectMenu, ComponentType.TextInput, ComponentType.ChannelSelect, ComponentType.CheckboxGroup);
        foreach (var label in labels)
        {
            label.Label.Length.Should().BeInRange(1, LfgFormUi.MaxLabelLength, label.Label);
            label.Description!.Length.Should().BeInRange(1, LfgFormUi.MaxDescriptionLength, label.Description);
        }

        var game = Input(modal, LfgForm.GameField);
        (game.MinLength, game.MaxLength, game.Required).Should().Be(((int?)LfgRules.GameNameMinLength, (int?)LfgRules.GameNameMaxLength, (bool?)true));
        game.Value.Should().BeNull("a new form is empty");
        var start = Input(modal, LfgForm.StartField);
        (start.MaxLength, start.Required).Should().Be(((int?)LfgForm.StartMaxLength, (bool?)false), "empty = now");
        start.Value.Should().BeNull();
        labels.Select(l => l.Component).OfType<TextInputComponent>().Should().NotContain(t => t.CustomId == LfgForm.DetailsField, "details have their own modal");

        var voice = Select(modal, LfgForm.VoiceField);
        (voice.MinValues, voice.MaxValues, voice.IsRequired).Should().Be((0, 1, false));
        voice.ChannelTypes.Should().Equal(ChannelType.Voice);
        voice.DefaultValues.Should().BeEmpty();

        var notices = Notices(modal);
        (notices.CustomId, notices.MinValues, notices.MaxValues, notices.IsRequired).Should().Be((LfgForm.NoticesField, (int?)0, (int?)2, (bool?)false));
        notices.Options.Select(o => (o.Value, o.DefaultState == true)).Should().Equal([(LfgFormUi.NotifyBefore, false), (LfgFormUi.NotifyStart, false)],
            "nothing is ticked on create");
    }

    [Fact]
    public void The_team_size_is_a_select_of_exactly_the_allowed_sizes()
    {
        var players = Select(LfgFormUi.Modal(Draft(), MaxPlayers, L("tr")), LfgForm.PlayersField);

        (players.Type, players.MinValues, players.MaxValues, players.IsRequired).Should().Be((ComponentType.SelectMenu, 1, 1, true));
        players.Options.Select(o => o.Value).Should().Equal(Enumerable.Range(LfgRules.MinPlayers, MaxPlayers - LfgRules.MinPlayers + 1).Select(n => n.ToString()));
        (players.Options.First().Value, players.Options.Last().Value).Should().Be(("2", "20"), "LfgRules.MinPlayers … Lfg:MaxPlayersPerListing");
        players.Options.Should().HaveCountLessThanOrEqualTo(LfgFormUi.MaxSelectOptions);
        players.Options.Should().NotContain(o => o.IsDefault == true, "a new form makes the user choose");
        LfgFormUi.PlayerChoices(LfgRules.HardMaxPlayers, null).Should().HaveCount(LfgRules.HardMaxPlayers - LfgRules.MinPlayers + 1,
            "never cut: a range that does not fit one select is refused by the options validation instead");
    }

    [Fact]
    public void A_team_size_range_that_does_not_fit_one_select_is_a_configuration_error()
    {
        var fits = LfgRules.MinPlayers + LfgFormUi.MaxSelectOptions - 1; // 26: 2…26 = 25 options

        LfgOptions.MaxTeamSizeChoices.Should().Be(LfgFormUi.MaxSelectOptions);
        new LfgOptions { MaxPlayersPerListing = fits }.Validate().Should().BeEmpty();
        new LfgOptions { MaxPlayersPerListing = fits + 1 }.Validate().Should().ContainSingle().Which.Should().Contain("Lfg:MaxPlayersPerListing");
        new LfgOptions { MaxPlayersPerListing = LfgRules.HardMaxPlayers }.Validate().Should().NotBeEmpty();
        new LfgOptions().Validate().Should().BeEmpty("the defaults fit");
    }

    [Fact]
    public void The_edit_form_is_the_same_form_filled_with_the_listing()
    {
        var values = new LfgFormValues("Deadlock", "12", "Casual", "05.10.2026 21:30", "2");
        var modal = LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, voice: new ChannelId(8802), before: true), MaxPlayers, L("tr"));

        modal.Title.Should().Be("Ekip İlanını Düzenle");
        (Input(modal, LfgForm.GameField).Value, Input(modal, LfgForm.StartField).Value).Should().Be(("Deadlock", "05.10.2026 21:30"));
        Select(modal, LfgForm.PlayersField).Options.Where(o => o.IsDefault == true).Select(o => o.Value).Should().Equal("12");
        Select(modal, LfgForm.VoiceField).DefaultValues.Should().ContainSingle().Which.Id.Should().Be(8802UL, "min 0 lets it be cleared");
        Notices(modal).Options.Select(o => o.DefaultState == true).Should().Equal(true, false);
        Notices(LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, atStart: true), MaxPlayers, L("tr"))).Options.Select(o => o.DefaultState == true)
            .Should().Equal(false, true);
        Notices(LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, before: true, atStart: true), MaxPlayers, L("tr"))).Options.Select(o => o.DefaultState == true)
            .Should().Equal(true, true);
        Labels(modal).Select(l => l.Component).OfType<TextInputComponent>().Should().NotContain(t => t.CustomId == LfgForm.DetailsField);

        var outside = Select(LfgFormUi.Modal(Draft(LfgFormKind.Edit, values with { Players = "30" }), MaxPlayers, L("tr")), LfgForm.PlayersField);
        outside.Options.Select(o => o.Value).Should().Contain("30", "a listing's own size stays selectable if the maximum was lowered; the server checks it");
        outside.Options.Single(o => o.IsDefault == true).Value.Should().Be("30");
    }

    [Fact]
    public void The_form_explains_every_field_in_turkish()
    {
        var modal = LfgFormUi.Modal(Draft(), MaxPlayers, L("tr"));
        var labels = Labels(modal);

        modal.Title.Should().Be("Ekip İlanı Oluştur");
        labels.Select(l => l.Label).Should().Equal("Oyun / Etkinlik", "Kişi Sayısı", "Başlangıç Tarihi", "Ses Kanalı", "Bildirimler");
        labels[1].Description.Should().Be("Sen dahil toplam ekip: 2–20");
        labels[2].Description.Should().Be("Boş = şimdi • Örn: 27.09.2026 21:30");
        Input(modal, LfgForm.StartField).Placeholder.Should().Be("27.09.2026 21:30");
        Notices(modal).Options.Select(o => o.Label).Should().Equal("⏰ 30 dk önce katılanları etiketle", "🚀 Başlangıçta katılanları etiketle");
        string.Join(" ", labels.Select(l => l.Description)).Should().NotContainAny("saat sonra", "30 dk sonra", "1 gün");
    }

    [Fact]
    public void The_submitted_selects_and_checkboxes_are_read_by_id()
    {
        IComponentInteractionData[] submitted =
        [
            Data(LfgForm.GameField), Data(LfgForm.PlayersField, "6"), Data(LfgForm.VoiceField, "8802"), Data(LfgForm.NoticesField, "before", "start"),
        ];

        LfgFormUi.ReadValue(submitted, LfgForm.PlayersField).Should().Be("6");
        LfgFormUi.ReadValue([Data(LfgForm.PlayersField)], LfgForm.PlayersField).Should().BeNull();
        LfgFormUi.ReadVoice(submitted).Should().Be(new ChannelId(8802));
        LfgFormUi.ReadVoice([Data(LfgForm.VoiceField)]).Should().BeNull("nothing selected = no voice channel (cleared)");
        LfgFormUi.ReadVoice([Data(LfgForm.VoiceField, "not-a-channel")]).Should().BeNull();
        LfgFormUi.ReadNotices(submitted).Should().Be((true, true));
        LfgFormUi.ReadNotices([Data(LfgForm.NoticesField, "before")]).Should().Be((true, false));
        LfgFormUi.ReadNotices([Data(LfgForm.NoticesField, "start")]).Should().Be((false, true));
        LfgFormUi.ReadNotices([Data(LfgForm.NoticesField)]).Should().Be((false, false), "nothing ticked");
        LfgFormUi.ReadNotices([Data(LfgForm.GameField)]).Should().Be((false, false));
        LfgFormUi.ReadNotices([Data(LfgForm.NoticesField, "everyone")]).Should().Be((false, false), "unknown values are ignored");

        typeof(LfgFormModal).GetProperties().Select(p => p.GetCustomAttribute<ModalTextInputAttribute>()?.CustomId).OfType<string>()
            .Should().BeEquivalentTo(LfgForm.GameField, LfgForm.StartField);
        typeof(LfgDetailsModal).GetProperties().Select(p => p.GetCustomAttribute<ModalTextInputAttribute>()?.CustomId).OfType<string>()
            .Should().Equal(LfgForm.DetailsField);
    }

    [Fact]
    public void The_details_modal_is_its_own_small_form()
    {
        var edit = Draft(LfgFormKind.Edit, new LfgFormValues("Deadlock", "6", "Rank fark etmez", null, "2"));
        var modal = LfgFormUi.DetailsModal(edit, L("tr"));

        modal.Title.Should().Be("İlan Detayı");
        modal.CustomId.Should().Be(LfgFormUi.DetailsModalPrefix + edit.Id);
        modal.CustomId.Length.Should().BeLessThanOrEqualTo(ComponentBuilder.MaxCustomIdLength);
        var label = Labels(modal).Should().ContainSingle().Subject;
        label.Label.Should().Be("Detay");
        var input = label.Component.Should().BeOfType<TextInputComponent>().Subject;
        (input.CustomId, input.Style, input.Required, input.Value).Should().Be((LfgForm.DetailsField, TextInputStyle.Paragraph, (bool?)false, "Rank fark etmez"));
        input.MaxLength.Should().Be(LfgRules.DetailsMaxLength * LfgFormUi.EditInputLengthFactor);
        var create = Labels(LfgFormUi.DetailsModal(Draft(), L("tr"))).Single().Component.Should().BeOfType<TextInputComponent>().Subject;
        create.MaxLength.Should().Be(LfgRules.DetailsMaxLength);
        create.Value.Should().BeNull();
    }

    [Fact]
    public void The_settings_step_holds_the_duration_the_details_and_the_actions()
    {
        var draft = Checked(Draft(voice: new ChannelId(8802), before: true) with { Values = LfgFormValues.Empty with { Details = "Casual" } }, T0.AddHours(2));

        var (content, components) = LfgFormUi.Settings(draft, T0, 120, L("tr"));

        content.Should().Contain("🎮 **Deadlock** · 👥 6 kişi").And.Contain("⏳ Süre: 2 saat").And.Contain("🔊 Ses Odası: <#8802>")
            .And.Contain("🔔 Bildirimler: ⏰ 30 dk önce katılanları etiketle").And.Contain("📝 Detay: Casual");
        var rows = components.Components.Cast<ActionRowComponent>().ToList();
        rows.Should().HaveCount(3);
        var duration = rows[0].Components.Should().ContainSingle().Which.Should().BeOfType<SelectMenuComponent>().Subject;
        (duration.CustomId, duration.MinValues, duration.MaxValues).Should().Be((LfgFormUi.DurationPrefix + draft.Id, 1, 1));
        duration.Options.Select(o => (o.Label, o.Value, o.IsDefault)).Should().Equal(
            ("1 saat", "1", (bool?)false), ("2 saat", "2", (bool?)true), ("3 saat", "3", (bool?)false));
        rows[1].Components.Cast<ButtonComponent>().Select(b => (b.Label, b.CustomId)).Should().Equal([("📝 Detayı Düzenle", LfgFormUi.DetailsPrefix + draft.Id)]);
        rows[2].Components.Cast<ButtonComponent>().Select(b => (b.Label, b.CustomId)).Should().Equal(
            ("İlanı Oluştur", LfgFormUi.SavePrefix + draft.Id), ("✏️ Ana Formu Düzenle", LfgFormUi.BackPrefix + draft.Id), ("İptal", LfgFormUi.CancelPrefix + draft.Id));
        rows.SelectMany(r => r.Components).OfType<SelectMenuComponent>().Should().ContainSingle("no voice or notice select in the settings step");
        rows.SelectMany(r => r.Components).OfType<IInteractableComponent>().Select(c => c.CustomId)
            .Should().OnlyContain(id => id.EndsWith(":" + draft.Id, StringComparison.Ordinal) && id.Length <= ComponentBuilder.MaxCustomIdLength);

        var (plain, buttons) = LfgFormUi.Settings(Checked(Draft(), null, details: null), T0, 120, L("tr"));
        plain.Should().Contain("📝 Detay eklenmedi").And.Contain("🔔 Bildirimler: yok").And.Contain("🗓️ Başlangıç: Şimdi");
        buttons.Components.Cast<ActionRowComponent>().ElementAt(1).Components.Cast<ButtonComponent>().Single().Label.Should().Be("📝 Detay Ekle");

        var edit = Checked(Draft(LfgFormKind.Edit, new LfgFormValues("Deadlock", "6", null, null, "2")), null, details: null);
        LfgFormUi.Settings(edit, T0, 120, L("tr")).Components.Components.Cast<ActionRowComponent>().Last().Components.Cast<ButtonComponent>()
            .Select(b => b.Label).Should().Equal("Kaydet", "✏️ Ana Formu Düzenle", "İptal");
    }

    [Fact]
    public void The_duration_offers_the_default_and_an_edited_listings_own_duration()
    {
        var create = LfgFormUi.Settings(Checked(Draft(), T0.AddHours(2), TimeSpan.FromMinutes(90)), T0, 90, L("tr")).Components;
        var options = create.Components.Cast<ActionRowComponent>().First().Components.OfType<SelectMenuComponent>().Single().Options;
        options.Select(o => (o.Label, o.Value, o.IsDefault)).Should().Equal(
            ("1 saat", "1", (bool?)false), ("2 saat", "2", (bool?)false), ("3 saat", "3", (bool?)false), ("1 saat 30 dk (varsayılan)", LfgFormUi.DefaultDuration, (bool?)true));
        LfgFormUi.DurationValue(LfgFormUi.DefaultDuration).Should().BeNull("the configured default");

        var edit = Draft(LfgFormKind.Edit, new LfgFormValues("Deadlock", "6", null, null, "90 dk"));
        var editOptions = LfgFormUi.Settings(Checked(edit, null, TimeSpan.FromMinutes(90)), T0, 120, L("tr")).Components.Components
            .Cast<ActionRowComponent>().First().Components.OfType<SelectMenuComponent>().Single().Options;
        editOptions.Select(o => (o.Label, o.Value, o.IsDefault)).Should().Equal(
            ("1 saat", "1", (bool?)false), ("2 saat", "2", (bool?)false), ("3 saat", "3", (bool?)false), ("1 saat 30 dk (mevcut)", "90 dk", (bool?)true));

        var afterChoice = edit with { Values = edit.Values with { Duration = "2" } };
        LfgFormUi.Settings(Checked(afterChoice, null), T0, 120, L("tr")).Components.Components.Cast<ActionRowComponent>().First().Components
            .OfType<SelectMenuComponent>().Single().Options.Select(o => (o.Value, o.IsDefault))
            .Should().Equal([("1", (bool?)false), ("2", (bool?)true), ("3", (bool?)false), ("90 dk", (bool?)false)], "the listing's own duration stays offered after another choice");
    }

    [Fact]
    public void The_settings_text_defuses_what_was_typed()
    {
        var draft = Draft(LfgFormKind.Edit) with
        {
            Preview = new LfgFormPreview("@everyone <@&1> Game", "<@123> **bold** https://evil.example", 5, LfgStart.AtInstant(T0.AddDays(1)), T0.AddDays(1),
                TimeSpan.FromMinutes(90)),
        };

        var (content, _) = LfgFormUi.Settings(draft, T0, 120, L("tr"));

        DiscordText.RawMentionPattern().IsMatch(content).Should().BeFalse();
        content.Should().NotContain("https://").And.Contain("<t:" + T0.AddDays(1).ToUnixTimeSeconds() + ":F>").And.Contain("1 saat 30 dk").And.Contain("Kaydet");
    }

    [Fact]
    public void A_stored_text_with_emoji_is_prefilled_whole()
    {
        var game = string.Concat(Enumerable.Repeat("🎮", LfgRules.GameNameMaxLength)); // 50 characters, 100 UTF-16 units
        var modal = LfgFormUi.Modal(Draft(LfgFormKind.Edit, new LfgFormValues(game, "6", null, null, "2")), MaxPlayers, L("tr"));

        Input(modal, LfgForm.GameField).Value.Should().Be(game, "never cut in the middle of a character");
        Input(modal, LfgForm.GameField).MaxLength.Should().Be(LfgRules.GameNameMaxLength * LfgFormUi.EditInputLengthFactor,
            "Discord counts UTF-16 units, the rule counts characters; the server applies the real limit");
        LfgRules.Length(game).Should().Be(LfgRules.GameNameMaxLength);
    }

    [Fact]
    public void Form_custom_ids_never_shadow_the_card_buttons()
    {
        string[] prefixes =
        [
            LfgCardRenderer.JoinPrefix, LfgCardRenderer.MaybePrefix, LfgCardRenderer.LeavePrefix, LfgCardRenderer.VoicePrefix, LfgCardRenderer.ClosePrefix,
            LfgCardRenderer.EditPrefix, LfgForm.ModalPrefix, LfgFormUi.DetailsModalPrefix, LfgFormUi.DurationPrefix, LfgFormUi.DetailsPrefix,
            LfgFormUi.SavePrefix, LfgFormUi.BackPrefix, LfgFormUi.CancelPrefix, LfgCommands.ConfirmClosePrefix, LfgCommands.KeepOpenPrefix,
        ];

        foreach (var a in prefixes)
            prefixes.Where(b => b != a).Should().NotContain(b => b.StartsWith(a, StringComparison.Ordinal), $"{a} would also match another handler");
    }

    /// <summary>
    /// The JSON Discord actually receives: the same conversion (<c>MessageComponentExtension.ToModel</c>) and serializer
    /// (<c>DiscordRestClient.Serializer</c>) Discord.Net 3.20.1 uses for an interaction response, reached by reflection.
    /// </summary>
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

    private static bool IsTrue(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void The_main_modal_goes_out_as_discords_documented_components(string language)
    {
        var values = new LfgFormValues("Deadlock", "6", null, "05.10.2026 21:30", "2");
        var wire = Wire(LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, voice: new ChannelId(8802), before: true), MaxPlayers, L(language)).Component.Components)
            .EnumerateArray().ToList();

        wire.Should().HaveCount(LfgFormUi.MaxModalComponents);
        foreach (var label in wire)
        {
            label.GetProperty("type").GetInt32().Should().Be(18, "Label");
            label.GetProperty("label").GetString()!.Length.Should().BeInRange(1, LfgFormUi.MaxLabelLength);
            label.GetProperty("description").GetString()!.Length.Should().BeInRange(1, LfgFormUi.MaxDescriptionLength);
            IsTrue(label.GetProperty("component"), "disabled").Should().BeFalse("modals cannot hold disabled components");
        }

        var parts = wire.Select(l => l.GetProperty("component")).ToList();
        parts.Select(c => c.GetProperty("type").GetInt32()).Should().Equal([4, 3, 4, 8, 22], "text input, string select, text input, channel select, checkbox group");
        parts.Select(c => c.GetProperty("custom_id").GetString()).Should().Equal(
            LfgForm.GameField, LfgForm.PlayersField, LfgForm.StartField, LfgForm.VoiceField, LfgForm.NoticesField);

        foreach (var input in new[] { parts[0], parts[2] })
        {
            input.GetProperty("style").GetInt32().Should().Be(1, "short");
            input.GetProperty("placeholder").GetString()!.Length.Should().BeInRange(1, 100);
            var max = input.GetProperty("max_length").GetInt32();
            max.Should().BeInRange(1, 4000);
            input.GetProperty("value").GetString()!.Length.Should().BeLessThanOrEqualTo(max);
            if (input.TryGetProperty("label", out var deprecated))
                deprecated.ValueKind.Should().Be(JsonValueKind.Null, "the deprecated text-input label is never set; the Label carries it");
        }

        var players = parts[1];
        (players.GetProperty("min_values").GetInt32(), players.GetProperty("max_values").GetInt32()).Should().Be((1, 1));
        var sizes = players.GetProperty("options").EnumerateArray().ToList();
        sizes.Should().HaveCount(MaxPlayers - LfgRules.MinPlayers + 1).And.HaveCountLessThanOrEqualTo(25);
        sizes.Select(o => o.GetProperty("value").GetString()).Should().Equal(Enumerable.Range(LfgRules.MinPlayers, MaxPlayers - LfgRules.MinPlayers + 1).Select(n => n.ToString()));
        sizes.Where(o => IsTrue(o, "default")).Select(o => o.GetProperty("value").GetString()).Should().Equal(["6"], "the listing's size is preselected");

        var voice = parts[3];
        (voice.GetProperty("min_values").GetInt32(), voice.GetProperty("max_values").GetInt32(), voice.GetProperty("required").GetBoolean())
            .Should().Be((0, 1, false), "optional: min_values 0 needs required false");
        voice.GetProperty("channel_types").EnumerateArray().Select(t => t.GetInt32()).Should().Equal([2], "GUILD_VOICE only");
        var chosen = voice.GetProperty("default_values")[0];
        (chosen.GetProperty("id").GetString(), chosen.GetProperty("type").GetString()).Should().Be(("8802", "channel"));

        var notices = parts[4];
        (notices.GetProperty("min_values").GetInt32(), notices.GetProperty("max_values").GetInt32(), notices.GetProperty("required").GetBoolean())
            .Should().Be((0, 2, false), "optional: min_values 0 needs required false");
        var ticks = notices.GetProperty("options").EnumerateArray().ToList();
        ticks.Should().HaveCount(2);
        ticks.Select(o => (o.GetProperty("value").GetString(), IsTrue(o, "default"))).Should().Equal([(LfgFormUi.NotifyBefore, true), (LfgFormUi.NotifyStart, false)]);
    }

    /// <summary>
    /// Discord's own checks, as it receives them: the client does not submit the modal while a required field is empty, a
    /// text is shorter than <c>min_length</c> or longer than <c>max_length</c>, or a required select has no choice. Nothing
    /// else can be checked before submit (no min/max value, pattern or custom validator exists for a text input).
    /// </summary>
    [Fact]
    public void The_create_form_carries_every_constraint_discord_checks_before_submit()
    {
        var parts = Wire(LfgFormUi.Modal(Draft(), MaxPlayers, L("tr")).Component.Components).EnumerateArray().Select(l => l.GetProperty("component")).ToList();

        var game = parts[0];
        (game.GetProperty("required").GetBoolean(), game.GetProperty("min_length").GetInt32(), game.GetProperty("max_length").GetInt32())
            .Should().Be((true, LfgRules.GameNameMinLength, LfgRules.GameNameMaxLength), "the same 2–50 the server applies");
        game.GetProperty("placeholder").GetString().Should().Be("Deadlock, CS2, Valheim…");

        var players = parts[1];
        (players.GetProperty("required").GetBoolean(), players.GetProperty("min_values").GetInt32(), players.GetProperty("max_values").GetInt32())
            .Should().Be((true, 1, 1), "a size must be chosen, and only one");
        players.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString())
            .Should().Equal(Enumerable.Range(2, 19).Select(n => n.ToString()), "234 is not a choice: it cannot be entered");

        var start = parts[2];
        (start.GetProperty("required").GetBoolean(), start.GetProperty("max_length").GetInt32(), start.GetProperty("placeholder").GetString())
            .Should().Be((false, LfgForm.StartMaxLength, "27.09.2026 21:30"), "empty = now; the date itself is checked after submit");
        if (start.TryGetProperty("min_length", out var startMin) && startMin.ValueKind == JsonValueKind.Number)
            startMin.GetInt32().Should().Be(0, "an empty start is allowed");
        foreach (var text in new[] { game, start })
            text.EnumerateObject().Select(p => p.Name).Should().NotContain(["min_value", "max_value", "pattern", "regex"], "no such text input fields exist");

        var voice = parts[3];
        (voice.GetProperty("required").GetBoolean(), voice.GetProperty("min_values").GetInt32(), voice.GetProperty("max_values").GetInt32())
            .Should().Be((false, 0, 1));
        voice.GetProperty("channel_types").EnumerateArray().Select(t => t.GetInt32()).Should().Equal([2]);

        var notices = parts[4];
        (notices.GetProperty("required").GetBoolean(), notices.GetProperty("min_values").GetInt32(), notices.GetProperty("max_values").GetInt32())
            .Should().Be((false, 0, 2));

        var details = Wire(LfgFormUi.DetailsModal(Draft(), L("tr")).Component.Components).EnumerateArray().Single().GetProperty("component");
        (details.GetProperty("required").GetBoolean(), details.GetProperty("max_length").GetInt32()).Should().Be((false, LfgRules.DetailsMaxLength));

        var duration = Wire(LfgFormUi.Settings(Checked(Draft(), null), T0, 120, L("tr")).Components.Components).EnumerateArray().First()
            .GetProperty("components")[0];
        duration.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()).Should().Equal(["1", "2", "3"], "999 is not a choice");
    }

    [Theory]
    [InlineData("tr", "lfg.create.date_format", "❌ **Başlangıç Tarihi**\nTarih/saat anlaşılamadı.\nÖrnek: `27.09.2026 21:30` veya `27.09.26 21:30`.")]
    [InlineData("tr", "lfg.create.date_not_future", "❌ **Başlangıç Tarihi**\nBaşlangıç tarihi gelecekte olmalı.")]
    [InlineData("tr", "lfg.create.players_range", "❌ **Kişi Sayısı**\nKişi sayısı 2–20 arasında olmalı.")]
    [InlineData("tr", "lfg.create.game_too_short", "❌ **Oyun / Etkinlik**\nEn az 2, en fazla 50 karakter olmalı.")]
    [InlineData("tr", "lfg.create.game_too_long", "❌ **Oyun / Etkinlik**\nEn az 2, en fazla 50 karakter olmalı.")]
    [InlineData("tr", "lfg.create.notice_needs_start", "❌ **Bildirimler**\nBildirim kullanmak için bir başlangıç tarihi seçmelisin.")]
    [InlineData("tr", "lfg.create.voice_invalid", "❌ **Ses Kanalı**\nSeçilen kanal bu sunucuda kullanılabilir bir ses kanalı değil.")]
    [InlineData("tr", "lfg.create.duration_invalid", "❌ **İlan Süresi**\nGeçersiz süre. İlan süresi olarak 1, 2 veya 3 saat seç.")]
    [InlineData("tr", "lfg.edit.start_locked", "❌ **Başlangıç Tarihi**\nEtkinlik başladıktan sonra başlangıç zamanı değiştirilemez.")]
    [InlineData("en", "lfg.create.date_format", "❌ **Start date**\nCould not read the date/time.\nExample: `27.09.2026 21:30` or `27.09.26 21:30`.")]
    [InlineData("en", "lfg.create.players_range", "❌ **Team size**\nTeam size must be 2–20.")]
    public void A_refusal_names_the_field_and_says_what_is_wrong(string language, string key, string expected)
    {
        object?[] args = key == "lfg.create.players_range" ? [2, 20] : key.StartsWith("lfg.create.game", StringComparison.Ordinal) ? [2] : [];

        LfgFormUi.Refusal(key, args, L(language)).Should().Be(expected);
        LfgFormUi.FieldOf(key).Should().NotBeNull();
    }

    [Fact]
    public void Every_listing_rule_a_form_field_can_break_has_its_field()
    {
        string[] fieldKeys =
        [
            "lfg.create.game_too_short", "lfg.create.game_too_long", "lfg.create.details_too_long", "lfg.create.players_range", "lfg.create.duration_invalid",
            "lfg.create.notice_needs_start", "lfg.create.voice_invalid", "lfg.create.date_format", "lfg.create.date_not_in_zone", "lfg.create.date_ambiguous",
            "lfg.create.date_not_future", "lfg.create.date_too_far", "lfg.create.timezone_invalid", "lfg.edit.start_locked",
            "lfg.edit.players_below_joined", "lfg.edit.expiry_passed",
        ];

        foreach (var key in fieldKeys)
        {
            var field = LfgFormUi.FieldOf(key);
            field.Should().NotBeNull(key);
            foreach (var language in new[] { "tr", "en" })
                Catalog.Get(language, field!).Should().NotBe(field, $"{field} is localized in {language}");
        }

        foreach (var key in new[] { "lfg.create.limit", "lfg.create.wrong_channel", "lfg.edit.forbidden", "lfg.edit.ended" })
            LfgFormUi.FieldOf(key).Should().BeNull(key);
    }

    [Fact]
    public void The_settings_step_goes_out_as_discords_select_and_button_json()
    {
        var draft = Checked(Draft(before: true), T0.AddHours(2));
        var rows = Wire(LfgFormUi.Settings(draft, T0, 120, L("tr")).Components.Components).EnumerateArray().ToList();

        rows.Should().HaveCount(3).And.OnlyContain(r => r.GetProperty("type").GetInt32() == 1, "action rows");
        var duration = rows[0].GetProperty("components")[0];
        (duration.GetProperty("type").GetInt32(), duration.GetProperty("min_values").GetInt32(), duration.GetProperty("max_values").GetInt32()).Should().Be((3, 1, 1));
        duration.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()).Should().Equal("1", "2", "3");
        rows[1].GetProperty("components").EnumerateArray().Select(b => b.GetProperty("custom_id").GetString()).Should().Equal([LfgFormUi.DetailsPrefix + draft.Id]);
        rows[2].GetProperty("components").EnumerateArray().Select(b => b.GetProperty("custom_id").GetString()).Should().Equal(
            LfgFormUi.SavePrefix + draft.Id, LfgFormUi.BackPrefix + draft.Id, LfgFormUi.CancelPrefix + draft.Id);
    }

    [Fact]
    public void The_details_modal_goes_out_as_one_label_with_a_paragraph()
    {
        var wire = Wire(LfgFormUi.DetailsModal(Draft(), L("tr")).Component.Components).EnumerateArray().Should().ContainSingle().Subject;

        wire.GetProperty("type").GetInt32().Should().Be(18, "Label");
        var input = wire.GetProperty("component");
        (input.GetProperty("type").GetInt32(), input.GetProperty("style").GetInt32(), input.GetProperty("custom_id").GetString(), input.GetProperty("max_length").GetInt32())
            .Should().Be((4, 2, LfgForm.DetailsField, LfgRules.DetailsMaxLength));
    }

    [Fact]
    public void A_refused_form_offers_the_way_back_into_it()
    {
        var draft = Draft();

        var buttons = LfgFormUi.Retry(draft, L("tr")).Components.Cast<ActionRowComponent>().Single().Components.Cast<ButtonComponent>().ToList();

        buttons.Select(b => (b.Label, b.CustomId)).Should().Equal(
            ("✏️ Formu Düzelt", LfgFormUi.BackPrefix + draft.Id), ("📝 Detay Ekle", LfgFormUi.DetailsPrefix + draft.Id), ("İptal", LfgFormUi.CancelPrefix + draft.Id));
    }

    private static IComponentInteractionData Data(string customId, params string[] values) => new FakeComponentData(customId, values);

    private sealed record FakeComponentData(string CustomId, IReadOnlyCollection<string> Values) : IComponentInteractionData
    {
        public ComponentType Type => ComponentType.SelectMenu;
        public IReadOnlyCollection<IChannel> Channels => [];
        public IReadOnlyCollection<IUser> Users => [];
        public IReadOnlyCollection<IRole> Roles => [];
        public IReadOnlyCollection<IGuildUser> Members => [];
        public string Value => "";
        public bool? BoolValue => null;
    }
}
