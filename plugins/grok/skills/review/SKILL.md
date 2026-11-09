---
name: review
description: Get an independent second opinion from Grok (xAI) on a plan, design, architecture, API shape, research report or decision before it is built or adopted. Use when the user asks for a Grok review, a second opinion or a cross-check, or when two reviewers or two options disagree. Not for routine per-commit code review.
---

# Independent review with Grok

Grok has not seen this conversation, the repository or your reasoning. That is
the point: it pushes back where you would rationalise. It is also the limit: it
judges only what you send it, and every call costs the user money.

## When it is worth it

Good fits:
- A plan, design or architecture before it gets built.
- The shape of an API, file format or game/system rule set, judged the way its
  users will meet it.
- A research report or recommendation before the user decides on it.
- A disagreement: two options, or two reviewers recommending different things.
- A fundamental technical claim you are unsure of and could confabulate.

Poor fits:
- Bug-hunting large diffs before every commit. It is slow and expensive, and
  without the surrounding code most findings are noise. Verify with tests,
  replays and measurements instead; save the outside eye for the design.
- "What does this code do?" Read the code.
- Anything that needs current facts (versions, prices, news). Its training data
  lags.

## 1. Ask before sending

Grok is a paid external service, and everything you send goes to xAI. Unless
the user already asked for a Grok review of this specific thing, ask first, in
a few lines:
- what you will send (which files or sections, roughly how many KB),
- which tool and reasoning effort,
- the expected wait (usually 2 to 10 minutes),
- that the packet holds no secrets, credentials, personal data or material
  under NDA. Remove them before sending.

Wait for a yes. A yes for one review does not cover the next one.

## 2. Build the packet

Write a self-contained brief, usually 2 to 10 KB. Grok sees nothing else, so it
needs:
- **Goal and constraints:** what the thing must achieve, for whom, within which
  limits.
- **The design itself, complete.** A summary that leaves out key facts produces
  findings about gaps that are not real. An API reviewed from its list of names
  alone gets flagged for features it already has.
- **Evidence:** measured numbers rather than impressions, and what has already
  been tried and ruled out, with the result.
- **Decisions already made, marked as fixed:** "Decided by the owner, not up
  for review: ...". Otherwise Grok spends its answer re-arguing them.
- **Numbered questions:** what you actually want judged.

For code, send the interfaces, the functions that matter and the design notes,
not a 50 KB diff.

Keep the review independent. Leave out other reviewers' verdicts, your own
preferred answer, and notes that hint at either. To have Grok judge between
options, present them neutrally (template D).

## 3. Frame the request

Put the reviewer role in `system`, and the brief and questions in `message`.
Ask for:
- an attack, not an appraisal ("attack this before it is built"). Without that
  framing the answer drifts into "here are several considerations",
- findings ranked by severity, each with the failure scenario, why it happens
  and a concrete fix,
- what is sound, so you know what not to touch,
- a verdict at the end,
- a word limit (300 to 700 words), which forces priorities.

Templates. Adapt them, keep them short.

**A. First review of a plan**
- system: "You are an independent senior reviewer of <domain> designs. Be
  critical and concrete; do not just agree."
- message: "<brief> ... Attack this design before it is built. Rank your
  findings by severity. For each, give the failure scenario, why it happens
  and a concrete fix. Then say what is sound. End with a verdict: build as is /
  build with changes (list them) / don't build. Under 500 words."

**B. Follow-up review**
"Second review. You rejected v1 because: <one line per finding>. Here is v2:
<brief>. Does v2 resolve each point? What is new or still wrong? Ranked,
concrete, under 450 words."

**C. Evidence check**
"Review this draft critically and independently. For each claim, is the
evidence enough? What second measurement would you require before believing
it? Be specific."

**D. Arbitration**
"Option A: <...>. Option B: <...>. One reviewer recommended one of them, the
implementer prefers the other. Judge each option on <criteria>, propose a
better one if you see it, and end with your pick and the deciding reason."
Do not say who prefers which option.

**E. A yes/no question against a text** (licence, spec, policy)
"Answer Yes / No / Unclear with a confidence percentage. Tie the reasoning to
the exact clauses. Say plainly where the text is ambiguous."

## 4. Make the call

- `grok_chat` with `reasoning_effort: "high"` and `temperature` 0.2 to 0.3.
  Use `"xhigh"` for a large or subtle brief where quality matters more than
  time.
- Never `grok_chat_fast` for a review. Without reasoning it misreads long
  documents, invents scenarios and contradicts its own examples.
- `grok_chat_multi_agent` only for a hard, high-stakes question the user agreed
  to pay extra for. Several agents cross-check one another, at many times the
  tokens.
- Expect 1 to 10 minutes. The server reports progress, so long thinks come
  back. To keep the session free in the meantime, make the call from a
  background subagent.
- Do not retry on your own; the server already retries transient errors. If a
  safety filter cuts the request off, send a smaller, more focused packet. Do
  not reword the content to get past the filter.

## 5. Treat findings as hypotheses

Grok is right often enough to be worth asking, and wrong often enough that
nothing goes in unchecked. Before adopting a finding, check it against the
code, the docs, a measurement or the source.

Typical misses:
- It criticises something that is already disabled, absent or handled, because
  it cannot see the setup.
- It contradicts a decision the user already made. Marking decisions as fixed
  in the brief prevents most of these.
- It reports a gap that is only missing from the excerpt you sent.
- It doubts or "corrects" a quote or source it cannot check.
- Its proposals are unmeasured. A mechanism it predicts may not work once
  built, so measure before committing to it.
- Its verdict can flip when you add context. A flip means the answer depends on
  that context; say so.

Its real catches tend to look like these, and they are worth a measurement:
- "this fix cannot have the effect you expect, because ...",
- two numbers in the brief that cannot both be true,
- a missing measurement,
- an edge case the plan forgot.

## 6. Combining with a second reviewer

When the stakes justify it, give a second reviewer the same questions,
independently and in parallel: a subagent that can read the code and run
checks. Do not show either one the other's answer.
- Both agree: a strong signal.
- They disagree: lay out both positions with your recommendation and let the
  user decide. Do not quietly pick one.
- To go against a reviewed recommendation the user already approved, ask the
  user first.

## 7. Report to the user

For each finding, report:
- where it applies (file, section, step),
- what it would cause,
- whether you checked it (confirmed, refuted or open, and how),
- whether you adopted or dropped it, and why.

Then give the verdict and what changed in the plan.

Be concrete. "The cache in step 3 is never invalidated on rename (confirmed:
`Store.cs:120` keys it by path)" helps the user. "Grok raised some concerns
about step 3" does not.
