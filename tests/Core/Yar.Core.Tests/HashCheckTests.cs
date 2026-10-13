namespace Yar.Core.Tests;

public sealed class HashCheckTests : IDisposable
{
    private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string OtherSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"yar-hash-{Guid.NewGuid():N}.bin");

    public HashCheckTests() => File.WriteAllText(_file, "abc");

    public void Dispose() => File.Delete(_file);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Sha256OfAbc)]
    [InlineData("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD \n")]
    [InlineData("sha256:" + Sha256OfAbc)]
    [InlineData(Sha256OfAbc + "  app.AppImage")]
    [InlineData(Sha256OfAbc + " *app.AppImage")]
    [InlineData("SHA256 (app.AppImage) = " + Sha256OfAbc)]
    [InlineData(OtherSha256 + "  other.AppImage\n" + Sha256OfAbc + "  app.AppImage\n")]
    public void The_hash_for_this_file_is_found_however_it_was_pasted(string pasted)
    {
        var (hash, problem) = HashCheck.Parse(pasted, "app.AppImage");

        Assert.Null(problem);
        Assert.Equal(new PublishedHash(HashKind.Sha256, Sha256OfAbc), hash);
    }

    [Theory]
    [InlineData("900150983cd24fb0d6963f7d28e17f72", HashKind.Md5, true)]
    [InlineData("a9993e364706816aba3e25717850c26c9cd0d89d", HashKind.Sha1, true)]
    [InlineData("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", HashKind.Sha512, false)]
    public async Task The_kind_of_hash_is_told_from_its_length_and_checked(string hex, HashKind kind, bool weak)
    {
        var (hash, _) = HashCheck.Parse(hex, "x");

        Assert.Equal(kind, hash!.Kind);
        Assert.Equal(weak, hash.Weak);
        Assert.True(await HashCheck.MatchesAsync(_file, hash, Token));
    }

    [Fact]
    public async Task A_file_that_differs_does_not_match()
    {
        Assert.True(await HashCheck.MatchesAsync(_file, new(HashKind.Sha256, Sha256OfAbc), Token));
        Assert.False(await HashCheck.MatchesAsync(_file, new(HashKind.Sha256, OtherSha256), Token));
        Assert.Equal(Sha256OfAbc, await HashCheck.ComputeAsync(_file, HashKind.Sha256, Token));
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361")]
    [InlineData(Sha256OfAbc + "0")]
    [InlineData(OtherSha256 + "  one.AppImage\n" + Sha256OfAbc + "  two.AppImage")]
    [InlineData(OtherSha256 + "  app.AppImage\n" + Sha256OfAbc + "  app.AppImage")]
    public void What_cannot_be_used_is_refused_with_a_reason(string pasted)
    {
        var (hash, problem) = HashCheck.Parse(pasted, "app.AppImage");

        Assert.Null(hash);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Nothing_pasted_is_neither_a_hash_nor_a_problem()
    {
        Assert.Equal((null, null), HashCheck.Parse("  \n ", "app.AppImage"));
    }
}
