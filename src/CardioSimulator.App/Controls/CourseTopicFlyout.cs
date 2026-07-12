using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Builds the two <b>independent</b> dropdowns — a <b>Тема</b> (topic) selector and a <b>Подтема</b>
/// (subtopic) selector — shared by the Course Constructor and Teaching top-bar panels. Both are flat
/// (no nested submenus). Choosing a Тема drives the separate Подтема dropdown, and the two course
/// shapes are told apart by which dropdowns are shown:
/// <list type="bullet">
/// <item>a <b>group</b> Тема (<c>Course → Тема → Подтема</c>) exposes its Подтемы in the second
/// dropdown;</item>
/// <item>a <b>leaf</b> Тема (<see cref="TopicEntry.IsLeaf"/>, <c>Course → Тема</c>) is itself content,
/// opened directly from the topic dropdown — the panels hide the Подтема dropdown for it.</item>
/// </list>
/// Lectures that belong to no Тема (loose/legacy content) are listed after the topics as flat
/// top-level entries so they stay reachable — the Тема dropdown never hides content. They disappear
/// once every lecture is filed under a Тема.
/// </summary>
internal static class CourseTopicFlyout
{
    /// <summary>
    /// Builds the flat <b>Тема</b> dropdown: one <see cref="MenuFlyoutItem"/> per <see cref="TopicEntry"/>
    /// in order (group and leaf alike; <paramref name="onTopic"/>), then any lecture with no Тема as a
    /// top-level entry (<paramref name="onLecture"/>) so loose content stays reachable.
    /// </summary>
    public static MenuFlyout BuildTopics(Course course, DomainLanguage language, Action<string> onTopic, Action<string> onLecture)
    {
        var russian = language == DomainLanguage.RU;
        var flyout = new MenuFlyout();
        foreach (var topic in course.Topics)
            flyout.Items.Add(Item(TopicName(topic, russian), topic.Id, onTopic));
        foreach (var lecture in UngroupedLectures(course))
            flyout.Items.Add(Item(LectureName(lecture, russian), lecture.Id, onLecture));
        return flyout;
    }

    /// <summary>The course's lectures that belong to no Тема (null topic, or a topic id with no
    /// matching definition — e.g. a lecture left loose while a course is being organized).</summary>
    public static IReadOnlyList<LectureEntry> UngroupedLectures(Course course)
    {
        var known = course.Topics.Select(t => t.Id).ToHashSet();
        return course.Lectures.Where(l => string.IsNullOrEmpty(l.Topic) || !known.Contains(l.Topic!)).ToList();
    }

    /// <summary>True when <paramref name="lecture"/> belongs to no known Тема of <paramref name="course"/>.</summary>
    public static bool IsUngrouped(Course course, LectureEntry lecture) =>
        string.IsNullOrEmpty(lecture.Topic) || course.Topics.All(t => t.Id != lecture.Topic);

    /// <summary>
    /// Builds the <b>Подтема</b> dropdown for a group Тема: one item per member Подтема.
    /// <paramref name="onSelect"/> opens the chosen subtopic. Empty for a leaf Тема or unknown id.
    /// </summary>
    public static MenuFlyout BuildSubtopics(Course course, string? topicId, DomainLanguage language, Action<string> onSelect)
    {
        var russian = language == DomainLanguage.RU;
        var flyout = new MenuFlyout();
        foreach (var lecture in Subtopics(course, topicId))
            flyout.Items.Add(Item(LectureName(lecture, russian), lecture.Id, onSelect));
        return flyout;
    }

    /// <summary>
    /// Builds a flat <b>lecture</b> dropdown listing every lecture in the course — used for a course
    /// with no Темы (the classic flat <c>Course → Lecture</c> shape), where the second selector is a
    /// plain lecture picker rather than a Подтема picker.
    /// </summary>
    public static MenuFlyout BuildLectures(Course course, DomainLanguage language, Action<string> onSelect)
    {
        var russian = language == DomainLanguage.RU;
        var flyout = new MenuFlyout();
        foreach (var lecture in course.Lectures)
            flyout.Items.Add(Item(LectureName(lecture, russian), lecture.Id, onSelect));
        return flyout;
    }

    /// <summary>The member Подтемы of the group Тема <paramref name="topicId"/> (empty for a leaf/unknown id).</summary>
    public static IReadOnlyList<LectureEntry> Subtopics(Course course, string? topicId) =>
        string.IsNullOrEmpty(topicId)
            ? Array.Empty<LectureEntry>()
            : course.Lectures.Where(l => l.Topic == topicId).ToList();

    private static MenuFlyoutItem Item(string text, string id, Action<string> onSelect)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onSelect(id);
        return item;
    }

    public static string TopicName(TopicEntry topic, bool russian) =>
        russian ? topic.NameRu ?? topic.TitleEn : topic.TitleEn;

    public static string LectureName(LectureEntry lecture, bool russian) =>
        russian ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;
}
