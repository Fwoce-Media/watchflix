using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace WatchFlix.Desktop;

/// <summary>
/// The look: cool near-black stock, projector-lamp amber, facts set in mono —
/// the palette of the 3.0 web pages.
/// </summary>
public static class Theme
{
    public static readonly Color VaultC = C("#101318");
    public static readonly Color ShelfC = C("#171b22");
    public static readonly Color RailC = C("#222831");
    public static readonly Color EdgeC = C("#2c333e");
    public static readonly Color PaperC = C("#e7e3da");
    public static readonly Color DimC = C("#838c99");
    public static readonly Color DimmerC = C("#5b6472");
    public static readonly Color LampC = C("#e8b341");
    public static readonly Color LampDimC = C("#8a6a26");
    public static readonly Color CertC = C("#c7573f");
    public static readonly Color GoodC = C("#6fae7d");

    public static readonly Brush Vault = B(VaultC), Shelf = B(ShelfC), Rail = B(RailC), Edge = B(EdgeC),
        Paper = B(PaperC), Dim = B(DimC), Dimmer = B(DimmerC), Lamp = B(LampC), LampDim = B(LampDimC),
        Cert = B(CertC), Good = B(GoodC), Clear = Brushes.Transparent,
        Scrim = B(Color.FromArgb(0xE6, 0x10, 0x13, 0x18)),
        HitTarget = B(Color.FromArgb(0x01, 0, 0, 0));

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Brush B(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Control templates, parsed at start-up. If anything in them ever fails to
    /// load, the app carries on with Windows' own controls rather than not
    /// starting at all.
    /// </summary>
    public static void Apply(Application app)
    {
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(Xaml);
            app.Resources.MergedDictionaries.Add(dictionary);
        }
        catch (Exception ex)
        {
            App.Log("Theme failed to load, using default controls: " + ex);
        }
    }

    public static Style? Style(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <SolidColorBrush x:Key="Vault" Color="#101318"/>
  <SolidColorBrush x:Key="Shelf" Color="#171b22"/>
  <SolidColorBrush x:Key="Rail" Color="#222831"/>
  <SolidColorBrush x:Key="Edge" Color="#2c333e"/>
  <SolidColorBrush x:Key="Paper" Color="#e7e3da"/>
  <SolidColorBrush x:Key="Dim" Color="#838c99"/>
  <SolidColorBrush x:Key="Lamp" Color="#e8b341"/>

  <!-- a focus ring that only appears when moving with the keyboard or a remote -->
  <Style x:Key="LampFocus">
    <Setter Property="Control.Template">
      <Setter.Value>
        <ControlTemplate>
          <Rectangle Stroke="#e8b341" StrokeThickness="2" RadiusX="4" RadiusY="4" Margin="-3" SnapsToDevicePixels="True"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- buttons: ghost by default -->
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="#838c99"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderBrush" Value="#2c333e"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="13,7"/>
    <Setter Property="FontSize" Value="12.75"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" Padding="{TemplateBinding Padding}"
                  SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="Center" RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="#838c99"/>
              <Setter Property="Foreground" Value="#e7e3da"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#222831"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.35"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="#e8b341"/>
    <Setter Property="BorderBrush" Value="#e8b341"/>
    <Setter Property="Foreground" Value="#171102"/>
    <Setter Property="FontWeight" Value="Bold"/>
    <Setter Property="FontSize" Value="13.5"/>
    <Setter Property="Padding" Value="20,10"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="3" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"
                              RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#f2c35a"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.35"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- a button that is just its content: cards, tabs, links -->
  <Style x:Key="Bare" TargetType="Button">
    <Setter Property="Foreground" Value="#e7e3da"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="VerticalContentAlignment" Value="Stretch"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"
                  BorderThickness="0">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.35"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ToolTip">
    <Setter Property="Background" Value="#222831"/>
    <Setter Property="Foreground" Value="#e7e3da"/>
    <Setter Property="BorderBrush" Value="#2c333e"/>
    <Setter Property="Padding" Value="8,5"/>
  </Style>

  <!-- text entry -->
  <Style TargetType="TextBox">
    <Setter Property="Foreground" Value="#e7e3da"/>
    <Setter Property="Background" Value="#101318"/>
    <Setter Property="BorderBrush" Value="#2c333e"/>
    <Setter Property="CaretBrush" Value="#e8b341"/>
    <Setter Property="SelectionBrush" Value="#e8b341"/>
    <Setter Property="Padding" Value="10,8"/>
    <Setter Property="FontSize" Value="13.5"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="3">
            <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="#8a6a26"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.5"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- drop-downs -->
  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="#e7e3da"/>
    <Setter Property="Padding" Value="10,6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="bd" Background="Transparent" Padding="{TemplateBinding Padding}">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#222831"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter Property="Foreground" Value="#e8b341"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="#e7e3da"/>
    <Setter Property="FontSize" Value="12.75"/>
    <Setter Property="Background" Value="#171b22"/>
    <Setter Property="BorderBrush" Value="#2c333e"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="10,0,30,0"/>
    <Setter Property="MinWidth" Value="120"/>
    <Setter Property="Height" Value="34"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton x:Name="tb" Focusable="False" ClickMode="Press" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="b" CornerRadius="3"
                          Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}">
                    <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"
                          Data="M0,0 L4,4 L8,0" Stroke="#838c99" StrokeThickness="1.6"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="b" Property="BorderBrush" Value="#838c99"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"
                              Content="{TemplateBinding SelectionBoxItem}"
                              ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                   IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
              <Border Background="#171b22" BorderBrush="#2c333e" BorderThickness="1" CornerRadius="3"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                      MaxHeight="{TemplateBinding MaxDropDownHeight}" Margin="0,3,0,0">
                <ScrollViewer>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="tb" Property="Opacity" Value="1"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- tick boxes -->
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="#838c99"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="box" Width="15" Height="15" CornerRadius="2" Background="#101318"
                    BorderBrush="#5b6472" BorderThickness="1" VerticalAlignment="Center">
              <Path x:Name="tick" Data="M2.5,7.5 L6,11 L12.5,3.5" Stroke="#171102" StrokeThickness="2"
                    Visibility="Collapsed" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
            </Border>
            <ContentPresenter Margin="9,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="False"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="box" Property="Background" Value="#e8b341"/>
              <Setter TargetName="box" Property="BorderBrush" Value="#e8b341"/>
              <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="box" Property="BorderBrush" Value="#e8b341"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- the auto-next switch in the player -->
  <Style x:Key="Switch" TargetType="CheckBox">
    <Setter Property="Foreground" Value="#838c99"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="track" Width="34" Height="18" CornerRadius="9" Background="#222831"
                    BorderBrush="#2c333e" BorderThickness="1" VerticalAlignment="Center">
              <Ellipse x:Name="knob" Width="12" Height="12" Fill="#838c99" HorizontalAlignment="Left" Margin="2,0,0,0"/>
            </Border>
            <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="False"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="track" Property="Background" Value="#8a6a26"/>
              <Setter TargetName="track" Property="BorderBrush" Value="#e8b341"/>
              <Setter TargetName="knob" Property="Fill" Value="#e8b341"/>
              <Setter TargetName="knob" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="knob" Property="Margin" Value="0,0,2,0"/>
              <Setter Property="Foreground" Value="#e7e3da"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- thin scroll bars -->
  <ControlTemplate x:Key="VBar" TargetType="ScrollBar">
    <Track x:Name="PART_Track" IsDirectionReversed="True">
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Border x:Name="t" Background="#222831" CornerRadius="3" Margin="2"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="t" Property="Background" Value="#2c333e"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </ControlTemplate>
  <ControlTemplate x:Key="HBar" TargetType="ScrollBar">
    <Track x:Name="PART_Track">
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Border x:Name="t" Background="#222831" CornerRadius="3" Margin="2"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="t" Property="Background" Value="#2c333e"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </ControlTemplate>
  <Style TargetType="ScrollBar">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Width" Value="10"/>
    <Setter Property="MinWidth" Value="10"/>
    <Setter Property="Template" Value="{StaticResource VBar}"/>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="MinWidth" Value="0"/>
        <Setter Property="Height" Value="10"/>
        <Setter Property="MinHeight" Value="10"/>
        <Setter Property="Template" Value="{StaticResource HBar}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- sliders: seek bar and volume -->
  <Style x:Key="TrackButton" TargetType="RepeatButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="IsTabStop" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="RepeatButton">
          <Border Background="Transparent">
            <Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="Slider">
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource LampFocus}"/>
    <Setter Property="IsMoveToPointEnabled" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Slider">
          <Grid Background="Transparent" MinHeight="18">
            <Track x:Name="PART_Track">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style="{StaticResource TrackButton}" Background="#e8b341" Command="{x:Static Slider.DecreaseLarge}"/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style="{StaticResource TrackButton}" Background="#3a424f" Command="{x:Static Slider.IncreaseLarge}"/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType="Thumb">
                      <Ellipse Width="13" Height="13" Fill="#e7e3da"/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>
""";
}
