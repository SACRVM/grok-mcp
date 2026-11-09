---
name: editor
description: Use Grok (xAI) as an outside editor (Lektor, Lektorat) for a book, novel, story, essay or other long-form text, chapter by chapter. Covers developmental edits (structure, plot, pacing, characters), line edits (voice, style, dialogue), beta-reader reactions and consistency checks, with a style sheet that carries decisions from one round to the next. Use when the user asks for editorial feedback, a Lektorat, or Grok's opinion on a chapter or manuscript.
---

# Grok as editor (Lektor)

A good editor is an outside eye. They read like a reader, name what does not
work and leave the voice alone. Grok reads the text fresh, without the author's
intentions in its head.

The work is split three ways:
- **Grok** reads and writes the notes.
- **You** prepare what Grok reads, keep the style sheet, check Grok's notes
  against the manuscript and put the decisions in front of the author.
- **The author** decides every change.

## Ground rules

- **The voice is not up for correction.** Deliberate style, such as sentence
  fragments, dialect, unusual punctuation, the chosen tense or point of view,
  goes into the style sheet marked "keep", and stays protected.
  - Grok suggests; it does not rewrite passages wholesale.
  - Never apply Grok's edits to the manuscript without the author's yes,
    either per change or for a batch the author approved.
- **Ask before sending.** The unpublished manuscript leaves the machine for
  xAI, and every call costs money. Before sending, say:
  - which chapters and roughly how many words,
  - which kind of edit,
  - the rough wait and cost. A chapter of about 5,000 words on `grok_chat` is
    usually a few minutes and costs cents, not dollars.

  Wait for a yes. A yes for one chapter is not a yes for the whole book.
- **Work in the manuscript's language.** Tell Grok to write its notes in the
  language of the text and to quote the text verbatim.

## The style sheet: the editor's memory

Grok's chat sessions live only in the server's memory and are gone after a
restart. The durable memory is therefore a file next to the manuscript, such as
`editing/style-sheet.md`.

Create it in the first session. Ask the author for the contents rather than
inventing them. Update it after each round with the decisions the author made.
It holds:
- genre, audience, intended tone, and what the book is for;
- point of view, tense and narrative distance;
- spelling conventions: quotation marks, numbers, foreign words, old or new
  orthography;
- characters (name, age, looks, how they talk), places and the timeline;
- deliberate quirks of the voice, marked "keep";
- decisions from earlier rounds ("the prologue stays", "no chapter titles").

Keep a short synopsis next to it, a few lines per chapter already edited. Grok
can then judge a chapter in context without receiving the whole book again.

## One kind of edit per pass

Mixing kinds produces a long, shallow list. Ask the author which pass this is.

1. **Developmental edit** (Strukturlektorat): does the chapter or the book
   work? Structure, plot logic, pacing, tension, motivation and character
   arcs, point-of-view discipline, scenes that do no work, and what is
   missing.
2. **Line edit** (stilistisches Lektorat): the sentence level. Rhythm,
   redundancy, filler, clichés, telling where showing would land, dialogue
   that rings false, repeated words and images, unclear references. Delivered
   as concrete before-and-after suggestions.
3. **Beta-reader reaction:** reading as a reader of the genre would. Where
   attention dropped, what confused, what was felt, which character convinced
   and which did not, what the reader expects next. Reactions only, no fixes.
   This pass is useful before a developmental edit.
4. **Consistency check:** against the style sheet and the synopsis. Names,
   ages, timeline, places, facts, point-of-view slips, spelling conventions.
   You can do much of this yourself; Grok adds an independent read.

Proofreading for typos and commas is not what Grok is for. You or a dedicated
tool can do it more cheaply.

## The packet

- **`system`:** the editor's role, the kind of edit, the output format and the
  language rule.
- **`message`**, in this order:
  - the style sheet;
  - the synopsis so far;
  - the author's own questions or worries about this chapter. Ask the author
    for them; they focus the edit;
  - the chapter text, unchanged. Number the paragraphs (¶1, ¶2, ...) if it is
    long, so notes can point at them.

Send one chapter, or one sequence of scenes, per call. Longer texts get
shallower notes.

A developmental edit of the whole book is possible: grok-4.7 takes 500k tokens
of context, enough for a novel. Above 200k prompt tokens the price doubles, so
send the whole manuscript only for a structural pass, and state the cost first.

## Framing that works

**System prompt** (adapt the genre and language):
"You are an experienced literary editor for <genre> in <language>. You edit;
you do not rewrite. The author's voice and every choice marked 'keep' in the
style sheet are not to be corrected. Be honest and specific, and praise only
what is earned. Write your notes in <language>, quoting the text verbatim."

**Developmental edit**, end of the message:
"Edit this chapter for structure and effect. Rank your notes by importance,
ten at most. For each, quote the passage (a few words), say what does not work
for the reader and why, and give a direction for a fix, not a rewrite. Then
say what works and must stay. End with one paragraph on what this chapter
needs most."

**Line edit**, end of the message:
"Line-edit this chapter. Give at most 25 suggestions, each as:
¶<n>: "original" → "suggestion", then the reason in one line. Suggest a
change only where it clearly improves the text, and leave the voice alone.
Then list recurring habits (words, constructions) with how often they occur."

**Beta reader**, end of the message:
"Read this as a reader of <genre> would. Going through the text in order, tell
me where your attention dropped, where you were confused, what you felt, what
you expect to happen next, and which character you believe and which not. No
advice."

**Next round:**
"Second round. Last time you noted: <one line each>. The author has revised.
Which notes are resolved, which remain, and what is new?"

## Settings

- **Tool:** `grok_chat`.
  - `reasoning_effort: "high"` for developmental edits and consistency checks;
    `"medium"` is enough for a beta-reader reaction.
  - `temperature` 0.4 to 0.6 for editorial judgement, 0.3 for a consistency
    check.
- **Never `grok_chat_fast`:** without reasoning it skims long text and misreads
  it.
- **Discussing one chapter back and forth:** a `session_id` such as
  `editor-<book>-ch07` keeps the thread.
  - It is lost when the server restarts.
  - Every client of the same server can see session ids, so keep anything
    private out of the id.

## Before the author sees the notes

- Check every note that makes a factual claim about the text against the
  manuscript: continuity, "this contradicts chapter 3", "this word appears 9
  times". Grok knows only what was sent, and can misremember or invent.
- Flag notes that go against a "keep" item or an earlier decision in the style
  sheet.
- Do not filter for taste. A note you disagree with on style still goes to the
  author, marked as Grok's opinion.

## Presenting the notes and applying them

1. Group the notes by importance, not by their order in the text.
2. Give each note:
   - where it applies: chapter, paragraph and quote,
   - what the problem is,
   - Grok's reasoning,
   - your check: verified, cannot verify, or contradicts the style sheet.
3. Present line edits as a list the author can accept or reject one by one.
4. Ask which ones to apply.
5. Apply exactly the accepted changes. Do not quietly "improve" other passages
   while you are at it.
6. Record any new decisions in the style sheet.
