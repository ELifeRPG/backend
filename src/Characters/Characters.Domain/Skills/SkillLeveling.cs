namespace ELifeRPG.Characters.Domain.Skills;

public static class SkillLeveling
{
    public const int MaxLevel = 100;

    public static long XpForNextLevel(int level) => (long)(100 * Math.Pow(1.05, level));

    public static int LevelForTotalXp(long totalXp)
    {
        var level = 1;
        var remaining = totalXp;

        while (level < MaxLevel)
        {
            var xpForNext = XpForNextLevel(level);
            if (remaining < xpForNext)
            {
                break;
            }

            remaining -= xpForNext;
            level++;
        }

        return level;
    }
}
