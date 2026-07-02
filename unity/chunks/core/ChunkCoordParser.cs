// Extracts a ChunkCoord from a chunk scene name like "Chunk_05_04".
//
// Kept prefix-agnostic on purpose: ChunkManager lets the user configure the
// sceneNamePrefix (default "Chunk_"), so we match on the trailing "_XX_YY" pair
// instead of hard-coding the prefix. That means a project using "Tile_05_04" or
// "World_A_05_04" still parses correctly as long as the last two underscore-
// separated groups are digits.

using System.Text.RegularExpressions;

namespace ProjectName.World
{
    public static class ChunkCoordParser
    {
        static readonly Regex Rx = new(@"_(\d+)_(\d+)$", RegexOptions.Compiled);

        public static bool TryParseFromSceneName(string sceneName, out ChunkCoord coord)
        {
            coord = default;
            if (string.IsNullOrEmpty(sceneName)) return false;
            var m = Rx.Match(sceneName);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, out int x)) return false;
            if (!int.TryParse(m.Groups[2].Value, out int y)) return false;
            coord = new ChunkCoord(x, y);
            return true;
        }
    }
}
