using System;
using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    public sealed class PromptOption
    {
        public string Label = "";
        public string Description = "";
    }

    // One of Claude's questions (AskUserQuestion), as the card shows it.
    public sealed class PromptQuestion
    {
        public string Header = "";
        public string Text = "";
        public bool MultiSelect;
        public List<PromptOption> Options = new List<PromptOption>();
    }

    // Answering Claude's questions on the card (act-from-notch spec §3), one at a time: an option picked (several, for a
    // multi-select) or your own text in Other…, then Next, and Send on the last. Answers() gives them in the documented
    // format: question text -> label, labels, or typed text. Held in memory only.
    public sealed class QuestionFlow
    {
        public const int MaxQuestions = HookReply.MaxQuestions, MaxOptions = 4, MaxOther = HookReply.MaxAnswer;

        readonly List<PromptQuestion> questions;
        readonly List<List<int>> chosen = new List<List<int>>();   // per question: the options picked, in the order Claude lists them
        readonly List<bool> otherOpen = new List<bool>();
        readonly List<string> otherText = new List<string>();
        int index;

        public QuestionFlow(List<PromptQuestion> questions)
        {
            this.questions = questions;
            foreach (PromptQuestion q in questions)
            {
                chosen.Add(new List<int>());
                otherOpen.Add(q.Options.Count == 0);
                otherText.Add("");
            }
        }

        // The questions in AskUserQuestion's input, or null when they aren't what the card can show: 1 to 4 questions,
        // each with its own text (no two alike) and 1 to 4 options with labels of at most MaxOther characters (an answer
        // can be no longer, and the hook refuses one that is).
        public static List<PromptQuestion> Parse(Dictionary<string, object> toolInput)
        {
            object[] raw = Json.Arr(Json.Get(toolInput, "questions"));
            if (raw == null || raw.Length == 0 || raw.Length > MaxQuestions) return null;
            var list = new List<PromptQuestion>();
            foreach (object item in raw)
            {
                var q = new PromptQuestion();
                q.Text = Json.Str(Json.Get(item, "question")) ?? "";
                q.Header = Json.Str(Json.Get(item, "header")) ?? "";
                object multi = Json.Get(item, "multiSelect");
                q.MultiSelect = multi is bool && (bool)multi;
                object[] options = Json.Arr(Json.Get(item, "options"));
                bool freeText = Json.Get(item, "freeText") is bool && (bool)Json.Get(item, "freeText");
                if (q.Text.Trim() == "" || options == null || (options.Length == 0 && !freeText) || options.Length > MaxOptions) return null;
                if (list.Any(other => other.Text == q.Text)) return null;
                foreach (object o in options)
                {
                    var option = new PromptOption();
                    option.Label = Json.Str(Json.Get(o, "label")) ?? "";
                    option.Description = Json.Str(Json.Get(o, "description")) ?? "";
                    if (option.Label.Trim() == "" || option.Label.Length > MaxOther) return null;
                    q.Options.Add(option);
                }
                list.Add(q);
            }
            return list;
        }

        public int Count { get { return questions.Count; } }
        public int Index { get { return index; } }
        public PromptQuestion Current { get { return questions[index]; } }
        public bool IsLast { get { return index == questions.Count - 1; } }
        public bool OtherOpen { get { return otherOpen[index]; } }
        public string OtherText { get { return otherText[index]; } }
        public bool IsChosen(int option) { return chosen[index].Contains(option); }

        // A single choice: this option, and only it (Other… closes). A multi-select: checked or unchecked.
        public void Choose(int option)
        {
            if (option < 0 || option >= Current.Options.Count) return;
            List<int> picked = chosen[index];
            if (!Current.MultiSelect)
            {
                picked.Clear();
                picked.Add(option);
                otherOpen[index] = false;
                return;
            }
            if (picked.Contains(option)) picked.Remove(option);
            else
            {
                picked.Add(option);
                picked.Sort();
            }
        }

        // A single choice: the box opens in place of the options. A multi-select: it opens or closes beside them.
        public void ToggleOther()
        {
            if (!Current.MultiSelect)
            {
                chosen[index].Clear();
                otherOpen[index] = true;
                return;
            }
            otherOpen[index] = !otherOpen[index];
        }

        public void SetOther(string text)
        {
            text = text ?? "";
            if (text.Length > MaxOther)
            {
                int cut = MaxOther;
                if (char.IsHighSurrogate(text[cut - 1])) cut--;   // never half of a pair
                text = text.Substring(0, cut);
            }
            otherText[index] = text;
        }

        public bool CanAdvance { get { return Answer(index) != null; } }

        // On to the next question: false on the last (it has Send instead), or when this one isn't answered yet.
        public bool Next()
        {
            if (IsLast || !CanAdvance) return false;
            index++;
            return true;
        }

        // Every answer, or null while any question has none.
        public Dictionary<string, object> Answers()
        {
            var answers = new Dictionary<string, object>();
            for (int i = 0; i < questions.Count; i++)
            {
                object answer = Answer(i);
                if (answer == null) return null;
                answers[questions[i].Text] = answer;
            }
            return answers;
        }

        // A single choice: the typed text, or the option's label. A multi-select: the labels, then the typed text. Null when
        // there is nothing.
        object Answer(int i)
        {
            PromptQuestion q = questions[i];
            string typed = otherOpen[i] ? otherText[i].Trim() : "";
            if (!q.MultiSelect)
            {
                if (otherOpen[i]) return typed != "" ? typed : null;
                return chosen[i].Count > 0 ? q.Options[chosen[i][0]].Label : null;
            }
            var labels = chosen[i].Select(o => q.Options[o].Label).ToList();
            if (typed != "") labels.Add(typed);
            return labels.Count > 0 ? labels.ToArray() : null;
        }
    }
}
