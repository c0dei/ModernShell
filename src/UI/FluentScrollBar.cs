using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace ModernShell.UI;

/// <summary>
/// Windows 11 風の細いスクロールバー（矢印なし・トラックなし・丸いつまみ。つまみはホバーで濃くなる）。
/// WPF 標準は古い見た目なので、アプリ全体の既定スタイルとして差し替える（<see cref="Install"/> を起動時に 1 度呼ぶ）。縦のスクロールバーだけ使っている。
/// </summary>
public static class FluentScrollBar
{
    private const string Xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='MinWidth' Value='10'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb Focusable='False' IsTabStop='False'>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border x:Name='bar' Margin='3,2,3,2' CornerRadius='2' Background='#66808080'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bar' Property='Background' Value='#B3808080'/><Setter TargetName='bar' Property='Margin' Value='2,2,2,2'/></Trigger>
                      <Trigger Property='IsDragging' Value='True'><Setter TargetName='bar' Property='Background' Value='#B3808080'/><Setter TargetName='bar' Property='Margin' Value='2,2,2,2'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    /// <summary>アプリ内のすべてのスクロールバー（ポップアップ・設定・メモ欄など）を、細い Fluent の見た目にする。</summary>
    public static void Install()
    {
        Application.Current.Resources[typeof(ScrollBar)] = (Style)XamlReader.Parse(Xaml);
    }
}
