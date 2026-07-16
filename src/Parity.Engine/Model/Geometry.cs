namespace Parity.Engine.Model;

/// <summary>方框:x,y 為相對各自 root 原點的座標(見規畫書 4.6)。</summary>
public readonly record struct Box(double X, double Y, double W, double H);

/// <summary>四邊內距。</summary>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    public static readonly Insets Zero = new(0, 0, 0, 0);
}
