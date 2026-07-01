using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Builds the nested "Тема → Подтема" dropdown shared by the Course Constructor and Teaching top-bar
/// selectors: each <see cref="TopicEntry"/> becomes a submenu that expands to its member lectures
/// (Подтемы), and clicking a lecture opens it via <c>onSelect</c>. Lectures with no topic (or a topic
/// id with no matching definition — e.g. legacy courses) are listed flat at the top level so they
/// stay reachable.
/// </summary>
internal static class CourseTopicFlyout
{
    public static MenuFlyout Build(Course course, DomainLanguage language, Action<string> onSelect)
    {
        var russian = language == DomainLanguage.RU;
        var flyout = new MenuFlyout();
        var known = course.Topics.Select(t => t.Id).ToHashSet();

        bool Ungrouped(LectureEntry l) => string.IsNullOrEmpty(l.Topic) || !known.Contains(l.Topic!);

        foreach (var lecture in course.Lectures.Where(Ungrouped))
            flyout.Items.Add(LeafItem(lecture, russian, onSelect));

        foreach (var topic in course.Topics)
        {
            var sub = new MenuFlyoutSubItem { Text = TopicName(topic, russian) };
            foreach (var lecture in course.Lectures.Where(l => l.Topic == topic.Id))
                sub.Items.Add(LeafItem(lecture, russian, onSelect));
            flyout.Items.Add(sub);
        }

        return flyout;
    }

    private static MenuFlyoutItem LeafItem(LectureEntry lecture, bool russian, Action<string> onSelect)
    {
        var captured = lecture;
        var item = new MenuFlyoutItem { Text = LectureName(lecture, russian) };
        item.Click += (_, _) => onSelect(captured.Id);
        return item;
    }

    public static string TopicName(TopicEntry topic, bool russian) =>
        russian ? topic.NameRu ?? topic.TitleEn : topic.TitleEn;

    public static string LectureName(LectureEntry lecture, bool russian) =>
        russian ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;
}
