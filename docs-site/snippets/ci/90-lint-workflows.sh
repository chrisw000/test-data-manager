# The CI guide's starter workflows must stay valid YAML with well-formed jobs/steps
# (actionlint-lite; PyYAML ships with mkdocs, so docs-verify always has it).
python - <<'PY'
import yaml

for path in ["docs-site/snippets/ci/starter-pr-gate.yml",
             "docs-site/snippets/ci/starter-nightly.yml"]:
    doc = yaml.safe_load(open(path, encoding="utf-8"))
    assert doc.get("name"), f"{path}: missing name"
    assert True in doc or "on" in doc, f"{path}: missing triggers"  # yaml 1.1 parses bare `on` as True
    jobs = doc.get("jobs") or {}
    assert jobs, f"{path}: no jobs"
    for job_name, job in jobs.items():
        assert "runs-on" in job, f"{path}:{job_name}: missing runs-on"
        steps = job.get("steps") or []
        assert steps, f"{path}:{job_name}: no steps"
        for step in steps:
            assert "uses" in step or "run" in step, f"{path}:{job_name}: step with neither uses nor run"
    print(f"{path}: OK ({len(jobs)} job(s))")
PY
