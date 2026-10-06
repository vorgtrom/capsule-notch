using System;
using System.Collections.Generic;

namespace Capsule
{
    public static class QuestionFlowTests
    {
        public static void Run()
        {
            ReadsClaudesQuestions();
            RefusesQuestionsTheCardCantShow();
            ASingleChoiceIsOneOptionOrYourOwnText();
            AMultiSelectIsSeveral();
            NextGoesOnOnlyWhenAnswered();
            AnswersAreInTheDocumentedFormat();
            ALabelTooLongToAnswerIsRefused();
            TypedTextIsNeverCutInTheMiddleOfACharacter();
            ABlankOtherAddsNothingToTheCheckedOptions();
            AnEarlierQuestionLeftUnansweredBlocksTheAnswers();
        }

        static void ALabelTooLongToAnswerIsRefused()
        {
            string head = "{\"questions\":[{\"question\":\"Q?\",\"options\":[{\"label\":\"", tail = "\"}]}]}";
            TestRunner.Check(QuestionFlow.Parse(Input(head + new string('a', QuestionFlow.MaxOther) + tail)) != null, "a label of exactly 2000 characters is fine");
            TestRunner.Check(QuestionFlow.Parse(Input(head + new string('a', QuestionFlow.MaxOther + 1) + tail)) == null, "one more and the question can't be answered, so it isn't shown");
        }

        // A character outside the basic plane is two chars: cutting between them would leave half of one.
        static void TypedTextIsNeverCutInTheMiddleOfACharacter()
        {
            QuestionFlow f = Flow();
            f.ToggleOther();
            string pair = char.ConvertFromUtf32(0x1F600);
            f.SetOther(new string('x', QuestionFlow.MaxOther - 1) + pair + "tail");
            string kept = f.OtherText;
            TestRunner.Check(kept.Length <= QuestionFlow.MaxOther, "the text is within its limit");
            TestRunner.Check(kept.Length == 0 || !char.IsHighSurrogate(kept[kept.Length - 1]), "and doesn't end with half a character");
            TestRunner.Eq(QuestionFlow.MaxOther - 1, kept.Length, "the whole pair is dropped, not half of it");
            f.SetOther(new string('x', QuestionFlow.MaxOther - 2) + pair + "tail");
            TestRunner.Eq(QuestionFlow.MaxOther, f.OtherText.Length, "when the pair fits it stays whole");
        }

        static void ABlankOtherAddsNothingToTheCheckedOptions()
        {
            QuestionFlow f = Flow();
            f.Choose(0);
            f.Next();
            f.Choose(0);
            f.Choose(2);
            f.ToggleOther();
            f.SetOther("   ");
            Dictionary<string, object> answers = f.Answers();
            var sizes = answers == null ? null : answers["Which sizes?"] as string[];
            TestRunner.Eq("S|L", sizes == null ? null : string.Join("|", sizes), "a blank Other… box beside checked options adds nothing");
            f.Choose(0);
            f.Choose(2);
            TestRunner.Check(!f.CanAdvance && f.Answers() == null, "and with no option checked, it answers nothing");
        }

        // There is no way back to an earlier question, so this takes the step by hand: only the later answer counts for nothing.
        static void AnEarlierQuestionLeftUnansweredBlocksTheAnswers()
        {
            QuestionFlow f = Flow();
            typeof(QuestionFlow).GetField("index", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(f, 1);
            f.Choose(1);
            TestRunner.Check(f.CanAdvance, "the later question is answered");
            TestRunner.Check(f.Answers() == null, "but an earlier one that isn't blocks all the answers");
        }

        // Sample questions only.
        const string Sample = "{\"questions\":[" +
            "{\"question\":\"Which colour?\",\"header\":\"Colour\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"Warm\"},{\"label\":\"Blue\",\"description\":\"Cool\"}]}," +
            "{\"question\":\"Which sizes?\",\"header\":\"Sizes\",\"multiSelect\":true,\"options\":[{\"label\":\"S\"},{\"label\":\"M\"},{\"label\":\"L\"}]}]}";

        static Dictionary<string, object> Input(string json) { return Json.Obj(Json.Parse(json)); }

        static QuestionFlow Flow() { return new QuestionFlow(QuestionFlow.Parse(Input(Sample))); }

        static void ReadsClaudesQuestions()
        {
            List<PromptQuestion> qs = QuestionFlow.Parse(Input(Sample));
            TestRunner.Check(qs != null && qs.Count == 2, "two questions");
            if (qs == null || qs.Count != 2) return;
            TestRunner.Eq("Colour|Which colour?|False|2", qs[0].Header + "|" + qs[0].Text + "|" + qs[0].MultiSelect + "|" + qs[0].Options.Count, "the first: its header, its text, single choice, two options");
            TestRunner.Eq("Red|Warm", qs[0].Options[0].Label + "|" + qs[0].Options[0].Description, "an option's label and description");
            TestRunner.Check(qs[1].MultiSelect && qs[1].Options[2].Label == "L" && qs[1].Options[2].Description == "", "the second: a multi-select; a missing description is empty");
        }

        static void RefusesQuestionsTheCardCantShow()
        {
            TestRunner.Check(QuestionFlow.Parse(null) == null, "no input: no questions");
            TestRunner.Check(QuestionFlow.Parse(Input("{}")) == null, "no question list: none");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[]}")) == null, "an empty list: none");
            string one = "{\"question\":\"Q?\",\"header\":\"H\",\"options\":[{\"label\":\"A\"}]}";
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[" + one.Replace("Q?", "Q1") + "," + one.Replace("Q?", "Q2") + "," + one.Replace("Q?", "Q3") + "," + one.Replace("Q?", "Q4") + "]}")) != null, "four questions are fine");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[" + one.Replace("Q?", "Q1") + "," + one.Replace("Q?", "Q2") + "," + one.Replace("Q?", "Q3") + "," + one.Replace("Q?", "Q4") + "," + one.Replace("Q?", "Q5") + "]}")) == null, "five are more than Claude asks");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[" + one + "," + one + "]}")) == null, "two questions alike can't be answered apart");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[{\"question\":\"Q?\",\"options\":[{\"description\":\"no label\"}]}]}")) == null, "an option without a label: none");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[{\"question\":\"Q?\",\"options\":[]}]}")) == null, "a question without options: none");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[{\"question\":\" \",\"options\":[{\"label\":\"A\"}]}]}")) == null, "a blank question: none");
            TestRunner.Check(QuestionFlow.Parse(Input("{\"questions\":[{\"question\":\"Q?\",\"options\":[{\"label\":\"A\"},{\"label\":\"B\"},{\"label\":\"C\"},{\"label\":\"D\"},{\"label\":\"E\"}]}]}")) == null, "five options are more than Claude offers");
        }

        static void ASingleChoiceIsOneOptionOrYourOwnText()
        {
            QuestionFlow f = Flow();
            TestRunner.Check(!f.CanAdvance && !f.IsChosen(0) && !f.IsChosen(1) && !f.OtherOpen, "nothing chosen at first");
            f.Choose(1);
            TestRunner.Check(f.IsChosen(1) && f.CanAdvance, "an option chosen answers it");
            f.Choose(0);
            TestRunner.Check(f.IsChosen(0) && !f.IsChosen(1), "choosing another takes its place");
            f.ToggleOther();
            TestRunner.Check(f.OtherOpen && !f.IsChosen(0), "Other… opens the box in place of the options");
            TestRunner.Check(!f.CanAdvance, "and answers nothing until something is typed");
            f.SetOther("   ");
            TestRunner.Check(!f.CanAdvance, "spaces aren't an answer");
            f.SetOther("Teal");
            TestRunner.Check(f.CanAdvance && f.OtherText == "Teal", "typed text is");
            f.ToggleOther();
            TestRunner.Check(f.OtherOpen, "clicking Other… again keeps the box open");
            f.Choose(1);
            TestRunner.Check(!f.OtherOpen && f.IsChosen(1), "an option chosen closes it");
            f.ToggleOther();
            TestRunner.Eq("Teal", f.OtherText, "reopened, the box still has what was typed");
            f.SetOther(new string('x', QuestionFlow.MaxOther + 50));
            TestRunner.Eq(QuestionFlow.MaxOther, f.OtherText.Length, "typed text is kept to its limit");
        }

        static void AMultiSelectIsSeveral()
        {
            QuestionFlow f = Flow();
            f.Choose(0);
            f.Next();
            TestRunner.Check(f.Current.MultiSelect, "the second question is a multi-select");
            f.Choose(2);
            f.Choose(0);
            TestRunner.Check(f.IsChosen(0) && f.IsChosen(2) && !f.IsChosen(1), "options are checked one by one");
            f.Choose(2);
            TestRunner.Check(f.IsChosen(0) && !f.IsChosen(2), "and unchecked");
            f.ToggleOther();
            TestRunner.Check(f.OtherOpen && f.IsChosen(0), "Other… opens beside the checked options");
            f.ToggleOther();
            TestRunner.Check(!f.OtherOpen, "and closes again");
            f.Choose(0);
            TestRunner.Check(!f.CanAdvance, "nothing checked answers nothing");
        }

        static void NextGoesOnOnlyWhenAnswered()
        {
            QuestionFlow f = Flow();
            TestRunner.Eq(2, f.Count, "two questions to go through");
            TestRunner.Check(!f.IsLast && f.Index == 0, "the first isn't the last");
            TestRunner.Check(!f.Next() && f.Index == 0, "Next doesn't go on from an unanswered question");
            f.Choose(0);
            TestRunner.Check(f.Next() && f.Index == 1 && f.IsLast, "answered, it does; the second is the last");
            f.Choose(1);
            TestRunner.Check(!f.Next() && f.Index == 1, "there is no Next after the last: it has Send");
        }

        static void AnswersAreInTheDocumentedFormat()
        {
            QuestionFlow f = Flow();
            TestRunner.Check(f.Answers() == null, "no answers until every question has one");
            f.Choose(1);
            f.Next();
            f.Choose(2);
            f.Choose(0);
            f.ToggleOther();
            f.SetOther("  Extra large ");
            Dictionary<string, object> answers = f.Answers();
            TestRunner.Check(answers != null && answers.Count == 2, "an answer for each question");
            if (answers == null) return;
            TestRunner.Eq("Blue", answers["Which colour?"] as string, "a single choice is its label, keyed by the question's text");
            var sizes = answers["Which sizes?"] as string[];
            TestRunner.Eq("S|L|Extra large", sizes == null ? null : string.Join("|", sizes), "a multi-select is its labels in Claude's order, then the typed text, trimmed");
            object[] questions = Json.Arr(Json.Get(Input(Sample), "questions"));
            TestRunner.Check(HookReply.ValidAnswers(Json.Obj(Json.Parse(Json.Write(answers))), questions), "and the hook takes them as they come through the pipe");
            QuestionFlow typed = Flow();
            typed.ToggleOther();
            typed.SetOther("Teal");
            typed.Next();
            typed.Choose(1);
            Dictionary<string, object> mine = typed.Answers();
            TestRunner.Eq("Teal", mine == null ? null : mine["Which colour?"] as string, "a single choice can be typed text");
        }
    }
}
