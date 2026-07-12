using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The "unsaved changes" guard for the Course Constructor. Shown before an action that would discard
/// the open lecture's edits — switching course/Тема/Подтема, or leaving the constructor screen.
/// Returns whether the caller may proceed:
/// <list type="bullet">
/// <item><b>Save</b> → persists via <see cref="CourseConstructorViewModel.SaveAsync"/>, then proceed;</item>
/// <item><b>Don't save</b> → reverts via <see cref="CourseConstructorViewModel.DiscardChanges"/>, then proceed;</item>
/// <item><b>Cancel</b> → stay put (returns false).</item>
/// </list>
/// A no-op that returns true when there is nothing unsaved.
/// </summary>
internal static class UnsavedChangesDialog
{
    public static async Task<bool> ConfirmAsync(XamlRoot? root, CourseConstructorViewModel vm)
    {
        if (!vm.HasUnsavedChanges) return true;
        if (root is null) return true; // no UI context to prompt in — don't block navigation

        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorUnsavedTitle,
            Content = AppStrings.CourseCtorUnsavedBody,
            PrimaryButtonText = AppStrings.CommonSave,
            SecondaryButtonText = AppStrings.CommonDontSave,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await vm.SaveAsync();
                return true;
            case ContentDialogResult.Secondary:
                vm.DiscardChanges();
                return true;
            default:
                return false;
        }
    }
}
