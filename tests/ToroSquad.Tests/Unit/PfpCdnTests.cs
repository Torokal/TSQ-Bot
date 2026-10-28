using ToroSquad.Modules.Pfp.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The CDN URLs of TSQ Profil Görseli: Discord's official paths (Image Formatting reference), static pictures as PNG,
/// animated (<c>a_</c>) ones as animated WebP — never GIF, which WebP/AVIF uploads do not convert to cleanly — both at
/// size 2048, built only from a Discord hash (anything else never reaches a URL).
/// </summary>
public sealed class PfpCdnTests
{
    private const ulong Guild = 689812743242514448;
    private const ulong User = 80351110224678912;
    private const string Static = "1269e74af4df7417b13759eae50c83dc";
    private const string Animated = "a_1269e74af4df7417b13759eae50c83dc";

    [Fact]
    public void Static_pictures_are_png_at_2048()
    {
        PfpCdn.UserAvatar(User, Static).Should().Be($"https://cdn.discordapp.com/avatars/{User}/{Static}.png?size=2048");
        PfpCdn.MemberAvatar(Guild, User, Static).Should().Be($"https://cdn.discordapp.com/guilds/{Guild}/users/{User}/avatars/{Static}.png?size=2048");
        PfpCdn.UserBanner(User, Static).Should().Be($"https://cdn.discordapp.com/banners/{User}/{Static}.png?size=2048");
        PfpCdn.MemberBanner(Guild, User, Static).Should().Be($"https://cdn.discordapp.com/guilds/{Guild}/users/{User}/banners/{Static}.png?size=2048");
    }

    [Fact]
    public void Animated_pictures_stay_animated_as_webp_with_the_animated_flag()
    {
        PfpCdn.UserAvatar(User, Animated).Should().Be($"https://cdn.discordapp.com/avatars/{User}/{Animated}.webp?size=2048&animated=true");
        PfpCdn.MemberAvatar(Guild, User, Animated).Should().Be($"https://cdn.discordapp.com/guilds/{Guild}/users/{User}/avatars/{Animated}.webp?size=2048&animated=true");
        PfpCdn.UserBanner(User, Animated).Should().Be($"https://cdn.discordapp.com/banners/{User}/{Animated}.webp?size=2048&animated=true");
        PfpCdn.MemberBanner(Guild, User, Animated).Should().Be($"https://cdn.discordapp.com/guilds/{Guild}/users/{User}/banners/{Animated}.webp?size=2048&animated=true");
        new[] { PfpCdn.UserAvatar(User, Animated), PfpCdn.MemberBanner(Guild, User, Animated) }.Should().NotContain(u => u!.Contains(".gif", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("1269e74af4df7417b13759eae50c83dc?x=1")]
    [InlineData("a_1269E74AF4DF7417B13759EAE50C83DC")]
    [InlineData("https://example.com/x.png")]
    [InlineData("b_1269e74af4df7417b13759eae50c83dc")]
    public void Anything_but_a_discord_hash_never_becomes_a_url(string? hash)
    {
        PfpCdn.IsValidHash(hash).Should().BeFalse();
        PfpCdn.UserAvatar(User, hash).Should().BeNull();
        PfpCdn.MemberAvatar(Guild, User, hash).Should().BeNull();
        PfpCdn.UserBanner(User, hash).Should().BeNull();
        PfpCdn.MemberBanner(Guild, User, hash).Should().BeNull();
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/embed/avatars/0.png", true)]
    [InlineData("https://cdn.discordapp.com/embed/avatars/5.png", true)]
    [InlineData("https://cdn.discordapp.com/embed/avatars/5.png?size=2048", false)] // the default avatar is never "upscaled"
    [InlineData("https://cdn.discordapp.com/avatars/1/abc.png", false)]
    [InlineData("https://evil.example/embed/avatars/1.png", false)]
    [InlineData(null, false)]
    public void Only_discords_default_avatar_url_is_accepted_as_the_default(string? url, bool accepted) =>
        PfpCdn.IsDefaultAvatarUrl(url).Should().Be(accepted);

    [Theory]
    [InlineData(null, true, PfpScope.Auto)]
    [InlineData("otomatik", true, PfpScope.Auto)]
    [InlineData("genel", true, PfpScope.Global)]
    [InlineData("sunucu", true, PfpScope.Server)]
    [InlineData("Genel", false, PfpScope.Auto)]
    [InlineData("server", false, PfpScope.Auto)]
    public void Tur_accepts_exactly_the_three_choice_values(string? value, bool accepted, PfpScope expected)
    {
        PfpScopes.TryParse(value, out var scope).Should().Be(accepted);
        if (accepted)
            scope.Should().Be(expected);
    }
}
