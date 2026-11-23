"""
Generates SYNTHETIC office documents with invented facts for the Corporate acceptance run (Excel, Word, PowerPoint, e-mail with an
attachment, PDF) and the matching question list. Nothing here comes from a real company: names, orders and values are made up.

    python make-office-samples.py <output-folder>      (needs: python-docx openpyxl python-pptx reportlab)

Writes the documents into <output-folder>/docs and the expected answers into <output-folder>/questions.json.
"""
import json, os, sys, io
from email.message import EmailMessage
from email import policy

out = sys.argv[1] if len(sys.argv) > 1 else "office-samples"
docs = os.path.join(out, "docs"); os.makedirs(docs, exist_ok=True)

# ---- Excel ---------------------------------------------------------------------------------------------------------------
from openpyxl import Workbook
wb = Workbook(); ws = wb.active; ws.title = "Ordini"
ws.append(["Ordine", "Cliente", "Importo EUR", "Consegna", "Stato"])
clients = ["Bianchi Srl", "Verde Impianti", "Neri Logistica", "Gallo Componenti", "Moro Trasporti"]
for i in range(1, 41):
    ws.append([4400 + i, clients[i % 5], round(900 + i * 137.25, 2), f"2026-1{1 + i % 2}-{10 + i % 18:02d}", "Consegnato" if i % 3 else "In lavorazione"])
ws.append([4471, "Rossi Meccanica", 12480.50, "2026-12-15", "In ritardo"])
ws.append(["Totale", None, "=SUM(C2:C42)", None, None])
p = wb.create_sheet("Parametri")
p.append(["Parametro", "Valore"]); p.append(["Soglia sconto", "7,5%"]); p.append(["Responsabile ordini", "Giulia Ferri"])
wb.save(os.path.join(docs, "Ordini_2026.xlsx"))

# ---- Word ----------------------------------------------------------------------------------------------------------------
from docx import Document
d = Document(); d.add_heading("Contratto di fornitura n. 2026/118", 1)
d.add_paragraph("Tra Alfa Meccanica S.p.A. (Cliente) e Beta Componenti S.r.l. (Fornitore).")
d.add_heading("Art. 3 - Durata", 2); d.add_paragraph("Il contratto ha durata di 36 mesi a decorrere dal 1 marzo 2026.")
d.add_heading("Art. 7 - Penali", 2)
d.add_paragraph("In caso di ritardo nella consegna il Fornitore riconosce una penale del 2,5% del valore dell'ordine per ogni settimana di ritardo, fino a un massimo del 10%.")
d.add_heading("Art. 9 - Foro competente", 2); d.add_paragraph("Per ogni controversia e' competente in via esclusiva il Foro di Torino.")
t = d.add_table(rows=3, cols=3); t.style = "Table Grid"
for r, row in enumerate([["Componente", "Codice", "Prezzo unitario EUR"], ["Sensore di pressione", "ZX-200", "38,90"], ["Staffa di fissaggio", "KB-17", "4,25"]]):
    for c, v in enumerate(row): t.cell(r, c).text = v
d.save(os.path.join(docs, "Contratto_Fornitura.docx"))

# ---- PowerPoint (with speaker notes) -------------------------------------------------------------------------------------
from pptx import Presentation
pr = Presentation()
s1 = pr.slides.add_slide(pr.slide_layouts[0]); s1.shapes.title.text = "Progetto Aquila - Roadmap 2027"; s1.placeholders[1].text = "Direzione Ingegneria"
s2 = pr.slides.add_slide(pr.slide_layouts[1]); s2.shapes.title.text = "Milestone"
s2.placeholders[1].text = "M1 prototipo: 31 marzo 2027\nM2 validazione: 30 giugno 2027\nM3 produzione pilota: 15 ottobre 2027"
s2.notes_slide.notes_text_frame.text = "Budget approvato per il progetto Aquila: 450.000 EUR."
pr.save(os.path.join(docs, "Piano_Progetto.pptx"))

# ---- Attachment workbook + e-mail ----------------------------------------------------------------------------------------
wb2 = Workbook(); w2 = wb2.active; w2.title = "Specifiche"
w2.append(["Vite", "Coppia di serraggio"]); w2.append(["M6", "10 Nm"]); w2.append(["M8", "25 Nm"]); w2.append(["M10", "49 Nm"])
buf = io.BytesIO(); wb2.save(buf)
m = EmailMessage(policy=policy.SMTP)
m["From"] = "Laura Bianchi <laura.bianchi@example.test>"; m["To"] = "Qualita <qualita@example.test>"; m["Date"] = "Wed, 18 Nov 2026 16:40:00 +0100"
m["Subject"] = "Verbale riunione qualita 18 novembre"
m.set_content("Buonasera,\nin riunione e' stato deciso quanto segue.\nAzione: Marco Verdi sostituisce il fornitore Delta entro il 20 dicembre 2026.\nIn allegato le specifiche di serraggio.\nLaura")
m.add_attachment(buf.getvalue(), maintype="application", subtype="vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename="Specifiche_Serraggio.xlsx")
with open(os.path.join(docs, "Verbale_Riunione.eml"), "wb") as f: f.write(bytes(m))

# ---- PDF -----------------------------------------------------------------------------------------------------------------
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas
c = canvas.Canvas(os.path.join(docs, "Manuale_Tecnico.pdf"), pagesize=A4)
c.setFont("Helvetica-Bold", 16); c.drawString(60, 780, "Manuale tecnico pompa idraulica HP-30"); c.setFont("Helvetica", 11)
c.drawString(60, 750, "Capitolo 1 - Introduzione. Il presente manuale descrive la manutenzione ordinaria."); c.showPage()
c.setFont("Helvetica-Bold", 14); c.drawString(60, 780, "Capitolo 2 - Diagnostica"); c.setFont("Helvetica", 11)
c.drawString(60, 750, "Codice errore E-417: sovratemperatura del motore. Azione: verificare la pompa del liquido di raffreddamento.")
c.drawString(60, 720, "Codice errore E-502: perdita di pressione nel circuito idraulico."); c.showPage()
c.setFont("Helvetica-Bold", 14); c.drawString(60, 780, "Capitolo 3 - Dati di esercizio"); c.setFont("Helvetica", 11)
c.drawString(60, 750, "Pressione olio minima: 1,8 bar a 800 giri/min."); c.drawString(60, 730, "Temperatura massima di esercizio: 95 gradi Celsius."); c.save()

# ---- expected answers (checked against the answers, never produced by PaRuX) ----------------------------------------------
questions = [
    {"q": "Qual e' l'importo dell'ordine 4471 e quando e' prevista la consegna?", "all": ["12480", "2026-12-15"]},
    {"q": "Che penale prevede il contratto per il ritardo e qual e' il massimo?", "all": ["2,5", "10"]},
    {"q": "Qual e' il foro competente del contratto di fornitura?", "all": ["Torino"]},
    {"q": "Quanto costa il sensore di pressione ZX-200?", "all": ["38,90"]},
    {"q": "Quando scade la milestone M2 del progetto Aquila?", "all": ["30 giugno 2027"]},
    {"q": "Qual e' il budget approvato per il progetto Aquila?", "all": ["450.000"]},
    {"q": "Chi sostituisce il fornitore Delta e entro quando?", "all": ["Marco Verdi", "20 dicembre"]},
    {"q": "Qual e' la coppia di serraggio delle viti M8?", "all": ["25 Nm"]},
    {"q": "Cosa significa il codice errore E-417 e cosa devo verificare?", "all": ["sovratemperatura", "pompa"]},
    {"q": "Qual e' la pressione minima dell'olio a 800 giri/min?", "all": ["1,8"]},
]
with open(os.path.join(out, "questions.json"), "w", encoding="utf-8") as f: json.dump(questions, f, ensure_ascii=False, indent=1)
print("written", len(os.listdir(docs)), "documents and", len(questions), "questions in", out)
