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
/// The listing form as Discord receives it (Discord.Net 3.20 builders, Discord's documented modal limits: at most five
/// top-level components, Label ≤ 45 / description ≤ 100, text input placeholder ≤ 100, custom ids ≤ 100; modals cannot hold
/// disabled components; a channel select inside a Label may be optional with min 0) and as the Interaction Framework reads
/// it back: the same field ids, nothing but a random draft id in any custom id, every text defused.
/// </summary>
public sealed class LfgModalContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly LocalizationCatalog Catalog = new([new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization")]);
    private static readonly string[] TextFields = [LfgForm.GameField, LfgForm.PlayersField, LfgForm.DetailsField, LfgForm.StartField];

    private static LfgFormUi.Text L(string language) => (key, args) => Catalog.Get(language, key, args);

    private static LfgFormDraft Draft(LfgFormKind kind = LfgFormKind.Create, LfgFormValues? values = null, ChannelId? voice = null, bool before = false) =>
        new LfgFormDrafts(new FakeTimeProvider(T0)).Open(new ActorContext(new GuildId(1), new UserId(10), GuildPermission.ViewChannel, [], false, 1),
            new ChannelId(2), kind, kind == LfgFormKind.Edit ? 7 : null, values ?? LfgFormValues.Empty, before, false, voice);

    private static List<LabelComponent> Labels(Modal modal) => modal.Component.Components.Select(c => c.Should().BeOfType<LabelComponent>().Subject).ToList();

    private static List<(LabelComponent Label, TextInputComponent Input)> Inputs(Modal modal) =>
        Labels(modal).Take(TextFields.Length).Select(l => (l, l.Component.Should().BeOfType<TextInputComponent>().Subject)).ToList();

    private static SelectMenuComponent VoiceSelect(Modal modal) => Labels(modal)[^1].Component.Should().BeOfType<SelectMenuComponent>().Subject;

    private static LfgFormDraft Checked(LfgFormDraft draft, DateTimeOffset? eventAt, TimeSpan? duration = null) => draft with
    {
        Preview = new LfgFormPreview("Deadlock", "Casual", 6, eventAt is { } at ? LfgStart.AtInstant(at) : LfgStart.Now, eventAt, duration ?? TimeSpan.FromHours(2)),
    };

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
        var labels = Labels(modal);
        labels.Should().HaveCount(LfgFormUi.MaxModalComponents);
        foreach (var label in labels)
        {
            label.Label.Length.Should().BeInRange(1, LfgFormUi.MaxLabelLength, label.Label);
            label.Description!.Length.Should().BeInRange(1, LfgFormUi.MaxDescriptionLength, label.Description);
        }

        var inputs = Inputs(modal);
        inputs.Select(i => i.Input.CustomId).Should().Equal(TextFields, "game, players, details, start date — no duration in the modal");
        foreach (var (_, input) in inputs)
        {
            input.Placeholder!.Length.Should().BeInRange(1, TextInputBuilder.MaxPlaceholderLength, input.Placeholder);
            input.Value.Should().BeNull("a new form is empty");
        }

        var byId = inputs.ToDictionary(i => i.Input.CustomId, i => i.Input);
        (byId["game"].MinLength, byId["game"].MaxLength, byId["game"].Required).Should().Be(((int?)LfgRules.GameNameMinLength, (int?)LfgRules.GameNameMaxLength, (bool?)true));
        (byId["players"].MaxLength, byId["players"].Required).Should().Be(((int?)LfgForm.PlayersMaxLength, (bool?)true));
        (byId["details"].MaxLength, byId["details"].Required, byId["details"].Style).Should().Be(((int?)LfgRules.DetailsMaxLength, (bool?)false, TextInputStyle.Paragraph));
        (byId["start"].MaxLength, byId["start"].Required).Should().Be(((int?)LfgForm.StartMaxLength, (bool?)false), "empty = now");

        var voice = VoiceSelect(modal);
        (voice.Type, voice.CustomId, voice.MinValues, voice.MaxValues).Should().Be((ComponentType.ChannelSelect, LfgForm.VoiceField, 0, 1));
        voice.ChannelTypes.Should().Equal(ChannelType.Voice);
        voice.IsRequired.Should().BeFalse("the voice channel is optional");
        voice.DefaultValues.Should().BeEmpty();
        voice.Placeholder!.Length.Should().BeInRange(1, SelectMenuBuilder.MaxPlaceholderLength);
    }

    [Fact]
    public void The_create_form_explains_every_field_in_turkish()
    {
        var modal = LfgFormUi.Modal(Draft(), 20, L("tr"));
        var labels = Labels(modal);

        modal.Title.Should().Be("Ekip İlanı Oluştur");
        labels.Select(l => l.Label).Should().Equal("Oyun / Etkinlik", "Kişi sayısı", "Detay", "Başlangıç Tarihi", "Ses Kanalı");
        labels[1].Description.Should().Be("Sen dahil toplam ekip: 2–20");
        labels[3].Description.Should().Be("Boş = şimdi • Örn: 27.09.2026 21:30");
        Inputs(modal)[3].Input.Placeholder.Should().Be("27.09.2026 21:30");
        string.Join(" ", labels.Select(l => l.Description)).Should().NotContainAny("saat sonra", "30 dk", "2 saat", "1 gün");
    }

    [Fact]
    public void The_edit_form_is_the_same_form_filled_with_the_listing()
    {
        var values = new LfgFormValues("Deadlock", "6", "Casual oynayacağız", "05.10.2026 21:30", "2");
        var modal = LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, voice: new ChannelId(8802)), 20, L("tr"));

        modal.Title.Should().Be("Ekip İlanını Düzenle");
        Inputs(modal).Select(i => i.Input.Value).Should().Equal("Deadlock", "6", "Casual oynayacağız", "05.10.2026 21:30");
        Inputs(modal).Select(i => i.Input.CustomId).Should().Equal(TextFields, "create and edit share one form");
        VoiceSelect(modal).DefaultValues.Should().ContainSingle().Which.Id.Should().Be(8802UL, "the current voice channel is preselected; min 0 lets it be cleared");
    }

    [Fact]
    public void The_bound_modal_reads_exactly_the_text_fields_and_the_voice_select_is_read_by_id()
    {
        var bound = typeof(LfgFormModal).GetProperties().Select(p => p.GetCustomAttribute<ModalTextInputAttribute>()?.CustomId).OfType<string>();

        bound.Should().BeEquivalentTo(TextFields);
        new LfgFormModal { Game = "g", Players = "2", Details = "d", Start = "s" }.ToValues("3").Should().Be(new LfgFormValues("g", "2", "d", "s", "3"),
            "the duration is carried over from the settings step");
        LfgFormUi.ReadVoice([Data(LfgForm.GameField), Data(LfgForm.VoiceField, "8802")]).Should().Be(new ChannelId(8802));
        LfgFormUi.ReadVoice([Data(LfgForm.VoiceField)]).Should().BeNull("nothing selected = no voice channel (cleared)");
        LfgFormUi.ReadVoice([Data(LfgForm.GameField)]).Should().BeNull();
        LfgFormUi.ReadVoice([Data(LfgForm.VoiceField, "not-a-channel")]).Should().BeNull();
    }

    [Fact]
    public void The_settings_step_holds_the_duration_and_the_notices_and_carries_only_the_draft_id()
    {
        var draft = Checked(Draft(voice: new ChannelId(8802), before: true), T0.AddHours(2));

        var (content, components) = LfgFormUi.Settings(draft, T0, 120, L("tr"));

        content.Should().Contain("🎮 **Deadlock** · 👥 6 kişi").And.Contain("⏳ Süre: 2 saat").And.Contain("🔊 Ses Odası: <#8802>").And.Contain("İlanı Oluştur");
        var rows = components.Components.Cast<ActionRowComponent>().ToList();
        rows.Should().HaveCount(3);
        var duration = rows[0].Components.Should().ContainSingle().Which.Should().BeOfType<SelectMenuComponent>().Subject;
        (duration.CustomId, duration.MinValues, duration.MaxValues).Should().Be((LfgFormUi.DurationPrefix + draft.Id, 1, 1));
        duration.Options.Select(o => (o.Label, o.Value, o.IsDefault)).Should().Equal(
            ("1 saat", "1", (bool?)false), ("2 saat", "2", (bool?)true), ("3 saat", "3", (bool?)false));
        var notify = rows[1].Components.Should().ContainSingle().Which.Should().BeOfType<SelectMenuComponent>().Subject;
        notify.CustomId.Should().Be(LfgFormUi.NotifyPrefix + draft.Id);
        (notify.MinValues, notify.MaxValues).Should().Be((0, 2));
        notify.Options.Select(o => (o.Value, o.IsDefault)).Should().Equal(("before", (bool?)true), ("start", (bool?)false));
        rows[2].Components.Cast<ButtonComponent>().Select(b => b.CustomId).Should().Equal(
            LfgFormUi.SavePrefix + draft.Id, LfgFormUi.BackPrefix + draft.Id, LfgFormUi.CancelPrefix + draft.Id);
        rows.SelectMany(r => r.Components).OfType<SelectMenuComponent>().Should().NotContain(s => s.Type == ComponentType.ChannelSelect, "the voice channel is in the modal");
        rows.SelectMany(r => r.Components).OfType<IInteractableComponent>().Select(c => c.CustomId)
            .Should().OnlyContain(id => id.EndsWith(":" + draft.Id, StringComparison.Ordinal) && id.Length <= ComponentBuilder.MaxCustomIdLength);
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
        editOptions.Select(o => (o.Value, o.IsDefault)).Should().Equal(("1", (bool?)false), ("2", (bool?)false), ("3", (bool?)false), ("90 dk", (bool?)true));
    }

    [Fact]
    public void A_listing_that_starts_now_offers_no_notices()
    {
        var draft = Checked(Draft(), null);

        var (content, components) = LfgFormUi.Settings(draft, T0, 120, L("tr"));

        content.Should().Contain("🗓️ Başlangıç: Şimdi").And.Contain("yalnızca ileri bir başlangıç");
        components.Components.Cast<ActionRowComponent>().SelectMany(r => r.Components).OfType<SelectMenuComponent>()
            .Should().ContainSingle().Which.CustomId.Should().Be(LfgFormUi.DurationPrefix + draft.Id, "only the duration");
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
        var modal = LfgFormUi.Modal(Draft(LfgFormKind.Edit, new LfgFormValues(game, "6", null, null, "2")), 20, L("tr"));

        Inputs(modal)[0].Input.Value.Should().Be(game, "never cut in the middle of a character");
        Inputs(modal)[0].Input.MaxLength.Should().Be(LfgRules.GameNameMaxLength * LfgFormUi.EditInputLengthFactor,
            "Discord counts UTF-16 units, the rule counts characters; the server applies the real limit");
        Inputs(modal)[2].Input.MaxLength.Should().Be(LfgRules.DetailsMaxLength * LfgFormUi.EditInputLengthFactor);
        LfgRules.Length(game).Should().Be(LfgRules.GameNameMaxLength);
    }

    [Fact]
    public void Form_custom_ids_never_shadow_the_card_buttons()
    {
        string[] prefixes =
        [
            LfgCardRenderer.JoinPrefix, LfgCardRenderer.MaybePrefix, LfgCardRenderer.LeavePrefix, LfgCardRenderer.VoicePrefix, LfgCardRenderer.ClosePrefix,
            LfgCardRenderer.EditPrefix, LfgForm.ModalPrefix, LfgFormUi.NotifyPrefix, LfgFormUi.DurationPrefix, LfgFormUi.SavePrefix, LfgFormUi.BackPrefix,
            LfgFormUi.CancelPrefix, LfgCommands.ConfirmClosePrefix, LfgCommands.KeepOpenPrefix,
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

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void The_modal_goes_out_as_discords_label_text_input_and_channel_select_json(string language)
    {
        var values = new LfgFormValues("Deadlock", "6", "Casual", "05.10.2026 21:30", "2");
        var wire = Wire(LfgFormUi.Modal(Draft(LfgFormKind.Edit, values, voice: new ChannelId(8802)), 20, L(language)).Component.Components).EnumerateArray().ToList();

        wire.Should().HaveCount(LfgFormUi.MaxModalComponents);
        var ids = new List<string>();
        foreach (var label in wire)
        {
            label.GetProperty("type").GetInt32().Should().Be(18, "Label");
            label.GetProperty("label").GetString()!.Length.Should().BeInRange(1, LfgFormUi.MaxLabelLength);
            label.GetProperty("description").GetString()!.Length.Should().BeInRange(1, LfgFormUi.MaxDescriptionLength);
            var component = label.GetProperty("component");
            if (component.TryGetProperty("disabled", out var disabled))
                disabled.ValueKind.Should().BeOneOf([JsonValueKind.False, JsonValueKind.Null], "modals cannot hold disabled components");
        }

        foreach (var label in wire.Take(TextFields.Length))
        {
            var input = label.GetProperty("component");
            input.GetProperty("type").GetInt32().Should().Be(4, "Text Input inside the Label");
            input.GetProperty("style").GetInt32().Should().BeOneOf(1, 2);
            input.GetProperty("placeholder").GetString()!.Length.Should().BeInRange(1, 100);
            var max = input.GetProperty("max_length").GetInt32();
            max.Should().BeInRange(1, 4000);
            if (input.TryGetProperty("min_length", out var min))
                min.GetInt32().Should().BeInRange(0, max);
            input.GetProperty("value").GetString()!.Length.Should().BeLessThanOrEqualTo(max);
            if (input.TryGetProperty("label", out var deprecated))
                deprecated.ValueKind.Should().Be(JsonValueKind.Null, "the deprecated text-input label is never set; the Label carries it");
            ids.Add(input.GetProperty("custom_id").GetString()!);
        }

        ids.Should().Equal(TextFields);
        var voice = wire[^1].GetProperty("component");
        (voice.GetProperty("type").GetInt32(), voice.GetProperty("custom_id").GetString()).Should().Be((8, LfgForm.VoiceField), "Channel Select inside the Label");
        (voice.GetProperty("min_values").GetInt32(), voice.GetProperty("max_values").GetInt32(), voice.GetProperty("required").GetBoolean())
            .Should().Be((0, 1, false), "optional: min_values 0 needs required false");
        voice.GetProperty("channel_types").EnumerateArray().Select(t => t.GetInt32()).Should().Equal([2], "GUILD_VOICE only");
        var chosen = voice.GetProperty("default_values")[0];
        (chosen.GetProperty("id").GetString(), chosen.GetProperty("type").GetString()).Should().Be(("8802", "channel"));
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
        var notify = rows[1].GetProperty("components")[0];
        (notify.GetProperty("type").GetInt32(), notify.GetProperty("min_values").GetInt32(), notify.GetProperty("max_values").GetInt32()).Should().Be((3, 0, 2));
        notify.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()).Should().Equal("before", "start");
        rows[2].GetProperty("components").EnumerateArray().Select(b => b.GetProperty("custom_id").GetString()).Should().Equal(
            LfgFormUi.SavePrefix + draft.Id, LfgFormUi.BackPrefix + draft.Id, LfgFormUi.CancelPrefix + draft.Id);
    }

    [Fact]
    public void A_refused_form_offers_the_way_back_into_it()
    {
        var draft = Draft();

        var buttons = LfgFormUi.Retry(draft, L("tr")).Components.Cast<ActionRowComponent>().Single().Components.Cast<ButtonComponent>().ToList();

        buttons.Select(b => (b.Label, b.CustomId)).Should().Equal(("✏️ Formu Düzenle", LfgFormUi.BackPrefix + draft.Id), ("İptal", LfgFormUi.CancelPrefix + draft.Id));
    }

    private static IComponentInteractionData Data(string customId, params string[] values) => new FakeComponentData(customId, values);

    private sealed record FakeComponentData(string CustomId, IReadOnlyCollection<string> Values) : IComponentInteractionData
    {
        public ComponentType Type => ComponentType.ChannelSelect;
        public IReadOnlyCollection<IChannel> Channels => [];
        public IReadOnlyCollection<IUser> Users => [];
        public IReadOnlyCollection<IRole> Roles => [];
        public IReadOnlyCollection<IGuildUser> Members => [];
        public string Value => "";
        public bool? BoolValue => null;
    }
}
