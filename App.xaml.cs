using System.Windows;

namespace AutoAnime
{
    // 这里显式告诉编译器：我要继承的是 WPF 的 Application (System.Windows.Application)
    // 而不是 WinForms 的 Application，这样报错就消失了。
    public partial class App : System.Windows.Application
    {
    }
}