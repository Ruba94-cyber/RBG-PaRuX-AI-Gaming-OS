"""Deterministic synthetic RAG corpus: N markdown files, each carrying one unique, checkable fact in the middle of
filler prose, plus questions.json (question + expected substring + source file). No real or company data.
Usage: python -I make_rag_corpus.py --out <dir> [--files 30] [--questions 10]"""
import argparse, json, os, random

ap = argparse.ArgumentParser()
ap.add_argument("--out", required=True); ap.add_argument("--files", type=int, default=30); ap.add_argument("--questions", type=int, default=10)
a = ap.parse_args()
rnd = random.Random(20261122)
places = ["Quillfeather", "Marrowgate", "Tallowby", "Brindlemoor", "Ashcombe", "Velmora", "Dunharrow", "Pellwick", "Sorrelby", "Kestrelford"]
things = ["greenhouse", "ferry terminal", "signal tower", "grain archive", "tide gauge", "kiln hall", "weather station", "rope workshop"]
filler = ["The committee reviewed the maintenance schedule and noted no unusual wear.", "Visitors are asked to sign the logbook at the entrance.",
          "A seasonal report is circulated to all departments every quarter.", "Spare parts are stored in the east annex behind the loading dock.",
          "Routine inspections follow the checklist posted beside the main door.", "Volunteers rotate through the evening shift on a fixed roster."]
os.makedirs(a.out, exist_ok=True)
facts = []
for i in range(a.files):
    place, thing = places[i % len(places)], things[(i * 3) % len(things)]
    name = "doc%02d-%s-%s.md" % (i, place.lower(), thing.replace(" ", "-"))
    value = str(rnd.randint(101, 989))
    fact = "The %s %s at site %s-%d records a reference level of %s units." % (place, thing, place[:3].upper(), i, value)
    paras = []
    for p in range(8):
        paras.append(" ".join(rnd.choice(filler) + " Entry %d." % rnd.randint(1000, 9999) for _ in range(5)))
    paras.insert(4, fact)
    open(os.path.join(a.out, name), "w", encoding="utf8").write("# %s %s report %d\n\n" % (place, thing, i) + "\n\n".join(paras) + "\n")
    facts.append({"q": "What reference level does the %s %s at site %s-%d record?" % (place, thing, place[:3].upper(), i), "a": value, "doc": name})
rnd.shuffle(facts)
json.dump(facts[: a.questions], open(os.path.join(a.out, "questions.json"), "w"), indent=1)
print("wrote", a.files, "files and", min(a.questions, len(facts)), "questions to", a.out)
