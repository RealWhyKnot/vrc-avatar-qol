# Wiki source

The pages in this directory are the **source of truth** for the GitHub Wiki at https://github.com/RealWhyKnot/vrc-avatar-qol/wiki. The [wiki-sync workflow](../.github/workflows/wiki-sync.yml) mirrors this folder to the GitHub Wiki repo on every push to `main` that touches `wiki/**`.

**Do not edit pages on github.com.** Web edits will be overwritten on the next sync. Open a PR against this repo's `wiki/` folder instead.

`Home.md` is the wiki landing page. `_Sidebar.md` controls the right-hand navigation. Both filenames are conventions imposed by GitHub Wiki.

## One-time bootstrap

The GitHub Wiki repo (`vrc-avatar-qol.wiki.git`) only materialises after a maintainer creates the first page through the web UI. If the wiki-sync workflow logs a "wiki repo doesn't exist yet" warning, visit https://github.com/RealWhyKnot/vrc-avatar-qol/wiki, click **Create the first page**, save any placeholder text — it'll be overwritten by the next sync.
