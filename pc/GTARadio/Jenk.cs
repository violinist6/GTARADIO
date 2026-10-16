namespace GTARadio;

static class Jenk
{
    public static uint Hash(string s)
    {
        uint h = 0;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()))
        {
            h += b; h += h << 10; h ^= h >> 6;
        }
        h += h << 3; h ^= h >> 11; h += h << 15;
        return h;
    }
    public static uint Stream(string s) => Hash(s) & 0x1FFFFFFF;
}
