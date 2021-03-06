﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using CDBurnerXP.Forms;
using CDBurnerXP.IO;
using Microsoft.Win32;

namespace Ketarin.Forms
{
    /// <summary>
    /// This dialog offers a GUI for editing an application
    /// job's variables.
    /// </summary>
    public partial class EditVariablesDialog : PersistentForm
    {
        /// <summary>
        /// Allows to refresh the contents of the ListBox.
        /// </summary>
        private class VariableListBox : ListBox
        {
            public new void RefreshItems()
            {
                base.RefreshItems();
            }
        }

        private readonly ApplicationJob.UrlVariableCollection m_Variables;
        private readonly ApplicationJob m_Job;
        private bool m_Updating;
        private string m_MatchSelection;
        private int m_MatchPosition = -1;
        private BrowserPreviewDialog m_Preview;
        private Thread regexThread;
        private bool gotoMatch;

        private delegate UrlVariable VariableResultDelegate();

        #region Properties

        /// <summary>
        /// Gets or sets the user agent to use for requests.
        /// </summary>
        public string UserAgent
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the currently used match for a given
        /// start/end or regex.
        /// </summary>
        public string MatchSelection
        {
            get { return m_MatchSelection; }
            set
            {
                m_MatchSelection = value;
                cmnuCopyMatch.Enabled = !string.IsNullOrEmpty(m_MatchSelection);
                cmnuGoToMatch.Enabled = cmnuCopyMatch.Enabled;
            }
        }

        /// <summary>
        /// Gets an instance of the preview dialog
        /// </summary>
        protected BrowserPreviewDialog PreviewDialog
        {
            get { return this.m_Preview ?? (this.m_Preview = new BrowserPreviewDialog()); }
        }

        /// <summary>
        /// The variable which is currently being edited.
        /// </summary>
        protected UrlVariable CurrentVariable
        {
            get
            {
                if (InvokeRequired)
                {
                    return this.Invoke(new VariableResultDelegate(delegate() { return CurrentVariable; })) as UrlVariable;
                }

                return lbVariables.SelectedItem as UrlVariable;
            }
        }

        /// <summary>
        /// Gets or sets whether the dialog should be
        /// opened in read-only mode.
        /// </summary>
        public bool ReadOnly
        {
            get
            {
                return txtUrl.ReadOnly;
            }
            set
            {
                bool enable = !value;
                txtUrl.ReadOnly = value;
                txtFind.ReadOnly = value;
                txtRegularExpression.ReadOnly = value;
                chkRightToLeft.Enabled = enable;
                bAdd.Enabled = enable;
                bRemove.Enabled = enable;
                bOK.Enabled = enable;
                bOK.Visible = enable;
            }
        }

        #endregion

        public EditVariablesDialog(ApplicationJob job) : base()
        {
            InitializeComponent();
            AcceptButton = bOK;
            CancelButton = bCancel;

            m_Job = job;
            m_Variables = new ApplicationJob.UrlVariableCollection(job);
            // Get a copy of all variables
            foreach (KeyValuePair<string, UrlVariable> pair in job.Variables)
            {
                UrlVariable clonedVariable = pair.Value.Clone() as UrlVariable;
                m_Variables.Add(pair.Key, clonedVariable);
                clonedVariable.Parent = m_Variables;
            }
        }

        /// <summary>
        /// Prepare context menu as soon as handles have been created.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Turn off auto word selection (workaround .NET bug).
            this.rtfContent.AutoWordSelection = true;
            this.rtfContent.AutoWordSelection = false;

            ReloadVariables(true);

            if (m_Job != null && !string.IsNullOrEmpty(m_Job.Name))
            {
                this.Text = "Edit Variables - " + m_Job.Name;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Prevent an invalid operation exception because of automcomplete
            txtUrl.Dispose();
        }

        /// <summary>
        /// Re-populates the ListBox with the available variables.
        /// </summary>
        private void ReloadVariables(bool updateList)
        {
            List<string> appVarNames = new List<string>();
            if (updateList) lbVariables.Items.Clear();

            foreach (KeyValuePair<string, UrlVariable> pair in m_Variables)
            {
                if (updateList) lbVariables.Items.Add(pair.Value);
                appVarNames.Add(pair.Value.Name);
            }

            // Adjust context menus
            txtUrl.SetVariableNames(new[] { "category", "appname" }, appVarNames.ToArray());
        }

        /// <summary>
        /// Updates the interface according to the currently selected variable.
        /// </summary>
        private void UpdateInterface()
        {
            // Enable or disable controls if variables exist
            bool enable = (lbVariables.SelectedIndex >= 0);
            bRemove.Enabled = enable;
            lblUrl.Enabled = enable;
            txtUrl.Enabled = enable;
            bLoad.Enabled = enable;
            bPostData.Enabled = enable;
            lblFind.Enabled = enable;
            txtFind.Enabled = enable;
            bFind.Enabled = enable;
            rtfContent.Enabled = enable;
            bUseAsStart.Enabled = enable;
            bUseAsEnd.Enabled = enable;
            lblRegex.Enabled = enable;
            txtRegularExpression.Enabled = enable;
            chkRightToLeft.Enabled = enable;
            rbContentUrlStartEnd.Enabled = enable;
            rbContentText.Enabled = enable;
            rbContentUrlRegex.Enabled = enable;

            if (!enable) return;

            m_Updating = true;

            try
            {
                // Uodate controls which belong to the variable
                using (new ControlRedrawLock(this))
                {
                    // Set the auto complete of the URL text box
                    txtUrl.AutoCompleteMode = AutoCompleteMode.Suggest;
                    txtUrl.AutoCompleteSource = AutoCompleteSource.CustomSource;
                    AutoCompleteStringCollection urls = new AutoCompleteStringCollection();
                    urls.AddRange(DbManager.GetVariableUrls());
                    txtUrl.AutoCompleteCustomSource = urls;

                    // Set remaining controls
                    txtUrl.Text = CurrentVariable.Url;
                    chkRightToLeft.Checked = CurrentVariable.RegexRightToLeft;
                    txtRegularExpression.Text = CurrentVariable.Regex;

                    switch (CurrentVariable.VariableType)
                    {
                        case UrlVariable.Type.Textual:
                            rbContentText.Checked = true;
                            SetLayout(false, false, false);

                            rtfContent.Top = rbContentText.Bottom + rbContentText.Margin.Bottom + rtfContent.Margin.Top;
                            rtfContent.Height = lbVariables.Bottom - rtfContent.Top;
                            break;

                        case UrlVariable.Type.StartEnd:
                            rbContentUrlStartEnd.Checked = true;
                            SetLayout(true, false, true);

                            rtfContent.Top = txtFind.Bottom + txtFind.Margin.Bottom + rtfContent.Margin.Top;
                            rtfContent.Height = lbVariables.Bottom - rtfContent.Top;
                            break;

                        case UrlVariable.Type.RegularExpression:
                            rbContentUrlRegex.Checked = true;
                            SetLayout(false, true, true);

                            rtfContent.Top = txtRegularExpression.Bottom + txtRegularExpression.Margin.Bottom + rtfContent.Margin.Top;
                            rtfContent.Height = lbVariables.Bottom - rtfContent.Top;
                            break;
                    }
                }

                SetRtfContent();
                UpdateRegexMatches();
            }
            finally
            {
                m_Updating = false;
            }
        }

        private void lbVariables_SelectedIndexChanged(object sender, EventArgs e)
        {
            this.gotoMatch = true;
            UpdateInterface();
            txtUrl.Focus();
        }

        /// <summary>
        /// Makes sure, that the matched content is visible
        /// in the richtextbox.
        /// </summary>
        private void GoToMatch()
        {
            if (!string.IsNullOrEmpty(m_MatchSelection))
            {
                if (m_MatchPosition >= 0)
                {
                    rtfContent.SelectionStart = m_MatchPosition;
                    rtfContent.SelectionLength = m_MatchSelection.Length;
                    rtfContent.SelectionLength = 0;
                }
            }
        }

        /// <summary>
        /// Adjusts the RTF content based on the currently
        /// selected variable.
        /// </summary>
        private void SetRtfContent()
        {
            if (CurrentVariable.VariableType == UrlVariable.Type.Textual)
            {
                rtfContent.Text = CurrentVariable.TextualContent;
            }
            else
            {
                if (string.IsNullOrEmpty(CurrentVariable.TempContent) && !string.IsNullOrEmpty(txtUrl.Text))
                {
                    rtfContent.Text = string.Empty;
                    bLoad.PerformClick();
                }
                else
                {
                    rtfContent.Text = CurrentVariable.TempContent;
                }
                RefreshRtfFormatting();
            }
        }

        #region Layout options

        /// <summary>
        /// Helper function to hide certain parts of the layout
        /// </summary>
        /// <param name="startEnd">Hide or show the start/end buttons</param>
        /// <param name="regex">Hide or show the regular expression field</param>
        /// <param name="find">Hide or show the search box and URL field</param>
        private void SetLayout(bool startEnd, bool regex, bool findAndUrl)
        {
            txtRegularExpression.Visible = regex;
            chkRightToLeft.Visible = regex;
            lblRegex.Visible = regex;
            bUseAsEnd.Visible = startEnd;
            bUseAsStart.Visible = startEnd;
            txtFind.Visible = findAndUrl;
            lblFind.Visible = findAndUrl;
            bFind.Visible = findAndUrl;
            lblUrl.Visible = findAndUrl;
            txtUrl.Visible = findAndUrl;
            bLoad.Visible = findAndUrl;
            bPostData.Visible = findAndUrl;
            rtfContent.ReadOnly = findAndUrl || ReadOnly;
        }

        private void rbContentUrlStartEnd_CheckedChanged(object sender, EventArgs e)
        {
            if (m_Updating || !rbContentUrlStartEnd.Checked) return;

            CurrentVariable.VariableType = UrlVariable.Type.StartEnd;
            UpdateInterface();
        }

        private void rbContentUrlRegex_CheckedChanged(object sender, EventArgs e)
        {
            if (m_Updating || !rbContentUrlRegex.Checked) return;

            CurrentVariable.VariableType = UrlVariable.Type.RegularExpression;
            UpdateInterface();
        }

        private void rbContentText_CheckedChanged(object sender, EventArgs e)
        {
            if (m_Updating || !rbContentText.Checked) return;

            CurrentVariable.VariableType = UrlVariable.Type.Textual;
            MatchSelection = null;
            m_MatchPosition = -1;
            UpdateInterface();
        }

        #endregion

        private void bAdd_Click(object sender, EventArgs e)
        {
            using (NewVariableDialog dialog = new NewVariableDialog(m_Variables))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    m_Variables.Add(dialog.VariableName, new UrlVariable(dialog.VariableName, m_Variables));
                    ReloadVariables(true);
                    lbVariables.SelectedItem = m_Variables[dialog.VariableName];
                }
            }
        }

        private void bLoad_Click(object sender, EventArgs e)
        {
            // Load URL contents and show a wait cursor in the meantime
            Cursor = Cursors.WaitCursor;

            try
            {
                using (WebClient client = new WebClient(this.UserAgent))
                {
                    string expandedUrl = null;
                    string postData = null;

                    // Note: The Text property might modify the text value
                    using (ProgressDialog dialog = new ProgressDialog("Loading URL", "Please wait while the content is being downloaded..."))
                    {
                        dialog.OnDoWork = delegate
                        {
                            expandedUrl = CurrentVariable.ExpandedUrl;
                            if (dialog.Cancelled) return false;
                            client.SetPostData(CurrentVariable);
                            postData = client.PostData;
                            CurrentVariable.TempContent = client.DownloadString(new Uri(expandedUrl));
                            return true;
                        };
                        dialog.OnCancel = delegate {
                            dialog.Cancel();
                        };
                        dialog.ShowDialog(this);
                        
                        // Did an error occur?
                        if (!dialog.Cancelled && dialog.Error != null)
                        {
                            LogDialog.Log("Failed loading URL", dialog.Error);

                            // Check whether or not the URL is valid and show an error message if necessary
                            if (dialog.Error is ArgumentNullException || string.IsNullOrEmpty(expandedUrl))
                            {
                                MessageBox.Show(this, "The URL you entered is empty and cannot be loaded.", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            else if (dialog.Error is UriFormatException)
                            {
                                MessageBox.Show(this, "The specified URL '" + expandedUrl + "' is not valid.", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            else
                            {
                                MessageBox.Show(this, "The contents of the URL can not be loaded: " + dialog.Error.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }

                    rtfContent.Text = CurrentVariable.TempContent;
                    // For Regex: Go to match after thread finish
                    this.gotoMatch = true;
                    UpdateRegexMatches();
                    // For Start/End: Go to match now
                    RefreshRtfFormatting();

                    this.GoToMatch();

                    // Show page preview if desired
                    if (cmnuBrowser.Checked)
                    {
                        PreviewDialog.ShowPreview(this, expandedUrl, postData);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "An error occured when loading the URL: " + ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void bFind_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtFind.Text)) return;

            // Do not find the same string again...
            int searchPos = rtfContent.SelectionStart + 1;
            // ... and start from the beginning if necessary
            if (searchPos >= rtfContent.Text.Length)
            {
                searchPos = 0;
            }
            int pos = rtfContent.Find(txtFind.Text, searchPos , RichTextBoxFinds.None);

            if (pos < 0) return;
            // If a match has been found, highlight it
            rtfContent.SelectionStart = pos;
            rtfContent.SelectionLength = txtFind.Text.Length;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F2:
                    if (CurrentVariable != null)
                    {
                        NewVariableDialog dialog = new NewVariableDialog(m_Variables)
                        {
                            VariableName = this.CurrentVariable.Name,
                            Text = "Rename variable"
                        };
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            m_Variables.Remove(CurrentVariable.Name);
                            CurrentVariable.Name = dialog.VariableName;
                            m_Variables.Add(CurrentVariable.Name, CurrentVariable);
                            lbVariables.RefreshItems();
                            ReloadVariables(false);
                        }
                    }
                    break;

                case Keys.Enter:
                    // Do not push the OK button in certain occasions
                    if (lbVariables.Items.Count == 0)
                    {
                        bAdd.PerformClick();
                        return true;
                    }
                    else if (txtUrl.Focused)
                    {
                        bLoad.PerformClick();
                        return true;
                    }
                    else if (txtFind.Focus())
                    {
                        bFind.PerformClick();
                        return true;
                    }
                    break;

                case Keys.Control | Keys.G:
                    GoToMatch();
                    return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void txtUrl_TextChanged(object sender, EventArgs e)
        {
            CurrentVariable.Url = txtUrl.Text;
        }

        private void bUseAsStart_Click(object sender, EventArgs e)
        {
            CurrentVariable.StartText = rtfContent.SelectedText;
            RefreshRtfFormatting();
        }

        private void bUseAsEnd_Click(object sender, EventArgs e)
        {
            CurrentVariable.EndText = rtfContent.SelectedText;
            RefreshRtfFormatting();
        }

        /// <summary>
        /// Highlights the currently matched content (based 
        /// on regex or start/end) within the richtextbox.
        /// </summary>
        private void RefreshRtfFormatting(Match match = null)
        {
            if (string.IsNullOrEmpty(rtfContent.Text) || CurrentVariable.VariableType == UrlVariable.Type.Textual) return;

            using (new ControlRedrawLock(this))
            {
                // Determine scroll position
                User32.POINT scrollPos = new User32.POINT();
                User32.SendMessage(rtfContent.Handle, User32.EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPos);

                // Reset text area
                MatchSelection = string.Empty;
                m_MatchPosition = -1;
                rtfContent.SelectionStart = 0;
                rtfContent.SelectionLength = rtfContent.Text.Length;
                rtfContent.SelectionColor = SystemColors.WindowText;
                rtfContent.SelectionFont = rtfContent.Font;
                rtfContent.SelectionBackColor = SystemColors.Window;
                rtfContent.SelectionLength = 0;

                if (match != null)
                {
                    m_MatchPosition = match.Index;
                    rtfContent.SelectionStart = match.Index;
                    rtfContent.SelectionLength = match.Length;
                    rtfContent.SelectionColor = Color.White;
                    rtfContent.SelectionBackColor = Color.Blue;

                    if (match.Groups.Count > 1)
                    {
                        m_MatchPosition = match.Groups[1].Index;
                        rtfContent.SelectionStart = match.Groups[1].Index;
                        rtfContent.SelectionLength = match.Groups[1].Length;
                        rtfContent.SelectionColor = Color.White;
                        rtfContent.SelectionBackColor = Color.Red;
                    }

                    MatchSelection = rtfContent.SelectedText;
                    rtfContent.SelectionLength = 0;
                }
                else if (CurrentVariable.VariableType == UrlVariable.Type.StartEnd && !string.IsNullOrEmpty(CurrentVariable.StartText))
                {
                    // Highlight StartText with blue background
                    int pos = rtfContent.Text.IndexOf(CurrentVariable.StartText);
                    if (pos == -1)
                    {
                        pos = 0;
                    }
                    else
                    {
                        rtfContent.SelectionStart = pos;
                        rtfContent.SelectionLength = CurrentVariable.StartText.Length;
                        rtfContent.SelectionColor = Color.White;
                        rtfContent.SelectionBackColor = Color.Blue;
                    }

                    int matchStart = pos + CurrentVariable.StartText.Length;

                    if (!string.IsNullOrEmpty(CurrentVariable.EndText) && matchStart <= rtfContent.Text.Length)
                    {
                        pos = rtfContent.Text.IndexOf(CurrentVariable.EndText, matchStart);
                        if (pos >= 0)
                        {
                            // Highlight EndText with blue background if specified
                            m_MatchPosition = pos;
                            rtfContent.SelectionStart = pos;
                            rtfContent.SelectionLength = CurrentVariable.EndText.Length;
                            rtfContent.SelectionColor = Color.White;
                            rtfContent.SelectionBackColor = Color.Blue;

                            // Highlight match with red background
                            rtfContent.SelectionStart = matchStart;
                            rtfContent.SelectionLength = pos - matchStart;
                            MatchSelection = rtfContent.SelectedText;
                            rtfContent.SelectionColor = Color.White;
                            rtfContent.SelectionBackColor = Color.Red;
                            rtfContent.SelectionLength = 0;
                        }
                    }
                }

                // Restore scroll position
                User32.SendMessage(rtfContent.Handle, User32.EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPos);
            }
        }

        private void rtfContent_SelectionChanged(object sender, EventArgs e)
        {
            bool enable = (rtfContent.SelectionLength > 0);
            cmnuCopy.Enabled = enable;
            bUseAsEnd.Enabled = bUseAsStart.Enabled = enable;
        }

        private void bRemove_Click(object sender, EventArgs e)
        {
            if (CurrentVariable == null) return;

            m_Variables.Remove(CurrentVariable.Name);
            ReloadVariables(true);

            if (lbVariables.Items.Count > 0)
            {
                lbVariables.SelectedIndex = 0;
            }
            else
            {
                lbVariables.SelectedIndex = -1;
            }
            lbVariables_SelectedIndexChanged(this, null);
        }

        private void rtfContent_TextChanged(object sender, EventArgs e)
        {
            if (CurrentVariable != null && CurrentVariable.VariableType == UrlVariable.Type.Textual)
            {
                CurrentVariable.TextualContent = rtfContent.Text;
            }
        }

        private void txtRegularExpression_TextChanged(object sender, EventArgs e)
        {
            UpdateRegexMatches();
        }

        private void UpdateRegexMatches()
        {
            // "Disable" regex if empty
            if (string.IsNullOrEmpty(txtRegularExpression.Text))
            {
                CurrentVariable.Regex = string.Empty;
                RefreshRtfFormatting();
                return;
            }

            try
            {
                // Check if Regex is valid
                new Regex(txtRegularExpression.Text);

                // Set value
                CurrentVariable.Regex = txtRegularExpression.Text;
            }
            catch (ArgumentException)
            {
                // Make sure that user notices an error. Just showing error 
                // messages will be annoying.
                txtRegularExpression.BackColor = Color.Tomato;
                return;
            }

            txtRegularExpression.BackColor = SystemColors.Window;

            txtRegularExpression.HintText = "Evaluating...";

            // Cancel current evaluation and start new
            if (regexThread != null)
            {
                regexThread.Abort();
                regexThread = null;
            }
            regexThread = new Thread(this.EvaluateRegex);
            regexThread.Start(rtfContent.Text);
        }

        private void EvaluateRegex(object text)
        {
            try
            {
                try
                {
                    Regex regex = CurrentVariable.CreateRegex();
                    if (regex == null) return;

                    Match match = regex.Match(text as string);

                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        this.SetRegexResult(regex, match);
                    });
                }
                catch (UriFormatException ex)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, "The regular expression cannot be evaluated: " + ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                catch (ThreadAbortException)
                {
                    /* Thread aborted, no error */ 
                }
                finally
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        txtRegularExpression.HintText = string.Empty;
                    });
                }
            }
            catch (InvalidOperationException)
            {
                /* Ignore error if form is closed */ 
            }
        }

        private void SetRegexResult(Regex regex, Match match)
        {
            // Called from thread. Only execute if current regex matches
            // the just returned result.
            if (this.CurrentVariable.Regex != regex.ToString())
            {
                return;
            }

            txtRegularExpression.HintText = string.Empty;
            RefreshRtfFormatting(match);

            if (match.Success && this.gotoMatch)
            {
                this.gotoMatch = false;
                GoToMatch();
            }
        }

        private void chkRightToLeft_CheckedChanged(object sender, EventArgs e)
        {
            CurrentVariable.RegexRightToLeft = chkRightToLeft.Checked;
            RefreshRtfFormatting();
        }

        private void bOK_Click(object sender, EventArgs e)
        {
            // Delete empty variables
            List<string> toDelete = this.m_Variables.Where(pair => pair.Value.IsEmpty).Select(pair => pair.Key).ToList();
            while (toDelete.Count > 0) { m_Variables.Remove(toDelete[0]); toDelete.RemoveAt(0); }

            // Save results
            m_Job.Variables = m_Variables;
        }

        private void bPostData_Click(object sender, EventArgs e)
        {
            using (PostDataEditor editor = new PostDataEditor())
            {
                editor.PostData = CurrentVariable.PostData;
                if (editor.ShowDialog(this) == DialogResult.OK)
                {
                    CurrentVariable.PostData = editor.PostData;
                }
            }
        }

        #region Context menu

        private void cmnuCopy_Click(object sender, EventArgs e)
        {
            SafeClipboard.SetData(rtfContent.SelectedText, true);
        }

        private void cmnuPaste_Click(object sender, EventArgs e)
        {
            rtfContent.Paste();
        }

        private void cmnuCopyMatch_Click(object sender, EventArgs e)
        {
            SafeClipboard.SetData(MatchSelection, true);
        }

        private void cmnuGoToMatch_Click(object sender, EventArgs e)
        {
            GoToMatch();
        }

        private void cmnuWrap_Click(object sender, EventArgs e)
        {
            rtfContent.WordWrap = !cmnuWrap.Checked;
            cmnuWrap.Checked = rtfContent.WordWrap;
        }

        private void cmnuBrowser_Click(object sender, EventArgs e)
        {
            cmnuBrowser.Checked = !cmnuBrowser.Checked;
            if (cmnuBrowser.Checked)
            {
                if (m_Preview == null)
                {
                    PreviewDialog.VisibleChanged += this.PreviewDialog_VisibleChanged;
                }
                bLoad.PerformClick(); // Reload browser contents
            }
            else
            {
                PreviewDialog.Hide();
            }
        }

        private void PreviewDialog_VisibleChanged(object sender, EventArgs e)
        {
            cmnuBrowser.Checked = PreviewDialog.Visible;
        }

        #endregion
    }
}
