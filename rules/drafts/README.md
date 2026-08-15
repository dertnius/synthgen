# Drafts

Agent output lands here, and **nothing here is a rule**.

`prompts/draft-rules.md` reads `artifacts/survey.json` and writes one draft per table into
this directory. A person then reads it, deletes what is wrong, fixes the `kind` and
`threshold`, and moves what survives into `rules/gaps.yaml` behind a merge request.

The drafts themselves are gitignored — the reviewed rule file is the artifact worth keeping,
and a directory of half-considered proposals is not.

Nothing a draft says can reach a database on its own. Even after it is moved, a rule still
has to clear four independent checks before a row changes: the predicate is parsed and
rejected if it references another table, the columns are validated against the live schema,
the threshold blocks a predicate that matches more than expected, and a person reads the
frozen values at the gate.

That is why an agent is allowed to draft here at all.
