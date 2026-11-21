using Vapt.Core.Perf;

namespace Vapt.Core.Tests;

public class StandbyCacheTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void Only_worth_it_when_free_memory_is_low_and_cache_is_big()
    {
        // 8 GB, 300 MB livres e 3 GB de cache: o caso dos engasgos em jogo.
        Assert.True(StandbyCache.WorthIt(new MemoryLists(8 * Gb, 300L << 20, 3 * Gb, 0)));

        // 16 GB com 6 GB livres: limpar só joga cache fora.
        Assert.False(StandbyCache.WorthIt(new MemoryLists(16 * Gb, 6 * Gb, 8 * Gb, 0)));

        // Pouca memória livre, mas quase nenhum cache para devolver.
        Assert.False(StandbyCache.WorthIt(new MemoryLists(8 * Gb, 200L << 20, 400L << 20, 0)));
    }
}
