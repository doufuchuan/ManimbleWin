using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 「新建演示文稿」对话框的视图模型：保存位置 + 名称 + 主题。
    ///
    /// <para>为什么单独成类而不塞进 MainViewModel：对话框要能独立预览、独立校验，
    /// T05 的 XAML 只负责绑定这几个属性；真正的文件生成仍然只有 <see cref="NewDeckService"/> 一处。</para>
    ///
    /// <para>名称校验在输入时即时给出（同名文件夹已存在、非法字符、空名），
    /// 与 <see cref="NewDeckService.Create"/> 的返回文案保持一致，避免两套提示。</para>
    /// </summary>
    public class NewDeckDialogViewModel : ObservableObject
    {
        private string _parentDir = "";
        private string _name = "";
        private DeckTheme _selectedTheme;
        private string _error = "";

        /// <summary>默认名称（与原版 NewDeck.swift 的 "Untitled Deck" 一致）。</summary>
        public const string DefaultName = "Untitled Deck";

        public NewDeckDialogViewModel(string parentDir, string title, IEnumerable<DeckTheme> themes, DeckTheme preselect = null)
        {
            ParentDir = parentDir ?? "";
            _name = string.IsNullOrWhiteSpace(title) ? DefaultName : title.Trim();
            foreach (DeckTheme t in themes ?? Enumerable.Empty<DeckTheme>()) Themes.Add(t);
            _selectedTheme = preselect ?? Themes.FirstOrDefault();
            Validate();
        }

        /// <summary>项目保存位置（父目录）。</summary>
        public string ParentDir
        {
            get => _parentDir;
            set
            {
                if (!SetProperty(ref _parentDir, value)) return;
                RaisePropertyChanged(nameof(DeckPathPreview));
                Validate();
            }
        }

        /// <summary>项目文件夹名 = deck 名。</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (!SetProperty(ref _name, value)) return;
                RaisePropertyChanged(nameof(DeckPathPreview));
                Validate();
            }
        }

        /// <summary>可选主题（7 套，来自 engine/manimble/themes）。</summary>
        public ObservableCollection<DeckTheme> Themes { get; } = new ObservableCollection<DeckTheme>();

        /// <summary>选中的主题；决定 common.py 与 manim.cfg 的背景色。</summary>
        public DeckTheme SelectedTheme
        {
            get => _selectedTheme;
            set => SetProperty(ref _selectedTheme, value);
        }

        /// <summary>当前输入的问题（空字符串表示可以创建）。</summary>
        public string Error
        {
            get => _error;
            private set
            {
                if (!SetProperty(ref _error, value)) return;
                RaisePropertyChanged(nameof(HasError));
                RaisePropertyChanged(nameof(CanCreate));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(Error);

        /// <summary>「创建」按钮是否可用。</summary>
        public bool CanCreate => !HasError;

        /// <summary>将要生成的 deck 路径（界面上给个预览，让用户知道会写到哪）。</summary>
        public string DeckPathPreview
        {
            get
            {
                string parent = (_parentDir ?? "").Trim();
                string name = (_name ?? "").Trim();
                if (parent.Length == 0 || name.Length == 0) return "";
                try { return Path.Combine(parent, name, NewDeckService.DeckFolderName); }
                catch { return ""; }
            }
        }

        /// <summary>重新校验并刷新错误文案；返回是否可以创建。</summary>
        public bool Validate()
        {
            string parent = (_parentDir ?? "").Trim();
            string name = (_name ?? "").Trim();

            if (parent.Length == 0) { Error = "请选择保存位置"; return false; }
            if (name.Length == 0) { Error = "请填写演示文稿名称"; return false; }
            if (name == "." || name == "..") { Error = "名称不合法"; return false; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Error = "名称里不能包含这些字符：\\ / : * ? \" < > |";
                return false;
            }
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
            {
                Error = "名称不能以句点或空格结尾";
                return false;
            }
            if (NewDeckService.DeckExists(parent, name))
            {
                Error = $"「{name}」里已经有一个 deck 了。用「打开演示文稿或项目」打开它，或者换一个名称。";
                return false;
            }
            Error = "";
            return true;
        }

        /// <summary>
        /// 按当前输入生成 deck（不打开；打开由调用方 <c>MainViewModel.CreateAndOpenNewDeckAsync</c> 负责）。
        /// </summary>
        /// <returns>成功返回 null；失败返回原因。</returns>
        public string Create(out string deckPath)
        {
            deckPath = null;
            if (!Validate()) return Error;
            return NewDeckService.Create(_parentDir, _name, _selectedTheme, out deckPath);
        }
    }
}
