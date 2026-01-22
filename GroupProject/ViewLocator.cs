using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GroupProject.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GroupProject
{
    [RequiresUnreferencedCode(
        "Default implementation of ViewLocator involves reflection which may be trimmed away.",
        Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
    public class ViewLocator : IDataTemplate
    {
        /// <summary>
        /// Given a view model, returns the corresponding view if possible.
        /// </summary>
        
        public Control? Build(object? param)
        {
            if (param is null)
                return null;

            var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            var type = Type.GetType(name);

            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }

            return new TextBlock { Text = "Not Found: " + name };
        }

        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
        
    }
}
