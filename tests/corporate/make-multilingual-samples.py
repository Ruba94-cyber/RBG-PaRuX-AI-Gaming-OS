"""
Generates SYNTHETIC documents in German, French, Spanish and English (invented facts, no real company data) plus questions asked in
several languages, including cross-lingual ones (a question in one language about a document written in another).

    python make-multilingual-samples.py <office-samples-folder>

Adds the documents to <folder>/docs and appends the questions to <folder>/questions.json (created by make-office-samples.py).
Needs: python-docx openpyxl reportlab.
"""
import json, os, sys

out = sys.argv[1] if len(sys.argv) > 1 else "office-samples"
docs = os.path.join(out, "docs"); os.makedirs(docs, exist_ok=True)

# ---- German work instruction (Word) ----------------------------------------------------------------------------------------
from docx import Document
d = Document(); d.add_heading("Wartungsanleitung Hydraulikpumpe HP-30", 1)
d.add_paragraph("Diese Anleitung beschreibt die regelmäßige Wartung der Hydraulikpumpe HP-30.")
d.add_heading("Anzugsmomente", 2)
d.add_paragraph("Die Befestigungsschrauben M10 werden mit einem Anzugsmoment von 52 Nm angezogen. Die Schrauben M8 benötigen 25 Nm.")
d.add_heading("Wartungsintervalle", 2)
d.add_paragraph("Das Öl der Pumpe wird alle 500 Betriebsstunden gewechselt. Der Ölfilter wird bei jedem zweiten Ölwechsel erneuert.")
d.add_heading("Fehlercodes", 2)
d.add_paragraph("Fehlercode F-208: Überdruck im Hydraulikkreis. Maßnahme: Das Druckbegrenzungsventil prüfen und bei Bedarf ersetzen.")
d.save(os.path.join(docs, "Wartungsanleitung_Pumpe.docx"))

# ---- French maintenance contract (PDF) --------------------------------------------------------------------------------------
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas
c = canvas.Canvas(os.path.join(docs, "Contrat_Maintenance.pdf"), pagesize=A4)
c.setFont("Helvetica-Bold", 15); c.drawString(55, 780, "Contrat de maintenance n° 2027/044"); c.setFont("Helvetica", 11)
c.drawString(55, 750, "Article 2 - Durée. Le contrat est conclu pour une durée de 24 mois à compter du 1er avril 2027.")
c.drawString(55, 728, "Article 5 - Pénalités. En cas de retard d'intervention, le prestataire verse une pénalité de 1,5 % par jour de retard,")
c.drawString(55, 714, "plafonnée à 12 % du montant annuel.")
c.drawString(55, 692, "Article 8 - Juridiction. Tout litige relève de la compétence exclusive du tribunal de commerce de Lyon.")
c.save()

# ---- Spanish stock list (Excel) --------------------------------------------------------------------------------------------
from openpyxl import Workbook
wb = Workbook(); ws = wb.active; ws.title = "Inventario"
ws.append(["Referencia", "Descripción", "Stock (uds)", "Ubicación"])
for i, (ref, desc, stock, loc) in enumerate([("BR-4411", "Rodamiento 6204", 120, "Pasillo C-3"), ("BR-4412", "Rodamiento 6205", 380, "Pasillo C-7"),
                                             ("BR-4413", "Rodamiento 6206", 95, "Pasillo D-1"), ("SL-2200", "Junta tórica 40x3", 1500, "Pasillo A-9")]):
    ws.append([ref, desc, stock, loc])
wb.save(os.path.join(docs, "Inventario_Almacen.xlsx"))

# ---- English release notes (Word) -------------------------------------------------------------------------------------------
d = Document(); d.add_heading("Firmware 3.2 release notes", 1)
d.add_paragraph("Firmware 3.2 fixes the CAN bus-off recovery that could leave the gateway silent for up to 30 seconds.")
d.add_paragraph("The minimum bootloader version required to install firmware 3.2 is 2.1.")
d.add_paragraph("Known issue KI-77: the diagnostic session may time out after a cold start below minus 20 degrees Celsius.")
d.save(os.path.join(docs, "Firmware_3_2_Release_Notes.docx"))

questions = [
    {"q": "What is the tightening torque for the M10 mounting bolts in the pump maintenance manual?", "all": ["52"]},
    {"q": "Was bedeutet der Fehlercode F-208 und was ist zu prüfen?", "all": ["Überdruck", "Druckbegrenzungsventil"]},
    {"q": "Ogni quante ore di funzionamento va cambiato l'olio della pompa HP-30?", "all": ["500"]},
    {"q": "Quelle est la pénalité de retard prévue au contrat de maintenance et quel est le plafond ?", "all": ["1,5", "12"]},
    {"q": "Which court has jurisdiction over the French maintenance contract?", "all": ["Lyon"]},
    {"q": "¿Cuántas unidades hay en stock del rodamiento 6205 y en qué pasillo está?", "all": ["380", "C-7"]},
    {"q": "In che corsia del magazzino si trova il cuscinetto 6205 e quante unita ci sono?", "all": ["C-7", "380"]},
    {"q": "What does firmware 3.2 fix and which bootloader version is required?", "all": ["bus-off", "2.1"]},
    {"q": "Welche Mindestversion des Bootloaders benötigt die Firmware 3.2?", "all": ["2.1"]},
    {"q": "Quanto dura il contratto di manutenzione francese e da quando decorre?", "all": ["24", "2027"]},
]
qp = os.path.join(out, "questions.json")
existing = json.load(open(qp, encoding="utf-8")) if os.path.exists(qp) else []
existing = [e for e in existing if e["q"] not in {q["q"] for q in questions}]
json.dump(existing + questions, open(qp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("documents:", len(os.listdir(docs)), "questions:", len(existing) + len(questions))
