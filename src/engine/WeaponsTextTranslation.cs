using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Documents;

namespace CMOLocalizer
{
    /// <summary>
    /// R67：武器行绑定的翻译投影。
    ///
    /// 实测根因（build/weapons-flicker/deploy/D_*.png 六帧序列 + WEAPONS_DIAG SNAPSHOT）：
    /// 武器行的 Run.Text 来自 MultiBinding（Qty + Name + StringFormat " {0}x {1}"）。
    /// 文本由**绑定求值**直接产出，所以任何“写 Run.Text”的修法都会被下一次求值覆盖，
    /// 屏幕上就是逐行阶梯式的中英回闪（实测约 6 次/秒）。
    ///
    /// 正确做法是在求值链上翻译：把原 MultiBinding 的各个子绑定与 StringFormat 原样保留，
    /// 只在最后一步把求值结果交给词典。业务源（Qty/Name）不被读写，ConvertBack 明确拒绝。
    /// </summary>
    public sealed class WeaponsTextTranslationConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return CustomAppDomainManager.TranslateWeaponsBoundText(values, parameter, culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            // 只做显示投影：把译文写回业务源会让 Qty/Name 变成中文，破坏游戏逻辑。
            throw new NotSupportedException("Weapons text projection is one-way.");
        }
    }

    public sealed class WeaponsTextTranslationConverterSingle : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            object[] values = value == null ? new object[0] : new object[] { value };
            return CustomAppDomainManager.TranslateWeaponsBoundText(values, parameter, culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("Weapons text projection is one-way.");
        }
    }
}
