using System.IO;
using System.IO.Compression;
using System.Text;

namespace BeeX.DeskNest;

/// <summary>
/// 零依赖的最小 xlsx 写出器（OpenXML 内联字符串），供 OCR 表格结果导出 Excel。
/// 支持：全表居中 + 细边框样式、按内容自适应列宽、合并单元格（mergeCells）、数字写为数值单元格。
/// </summary>
static class ExcelExporter
{
    /// <param name="grid">完整网格（含被合并覆盖的空格子）</param>
    /// <param name="merges">合并区域列表（行列均为 0 基，含端点）</param>
    public static void Save(string path,IReadOnlyList<string[]> grid,IReadOnlyList<(int R1,int C1,int R2,int C2)> merges)
    {
        using var zip=new ZipArchive(File.Create(path),ZipArchiveMode.Create);
        Write(zip,"[Content_Types].xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Write(zip,"_rels/.rels","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Write(zip,"xl/workbook.xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"BeeX OCR\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Write(zip,"xl/_rels/workbook.xml.rels","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        // 样式：s=1 居中+细边框；s=2 加粗居中+细边框（首行表头）
        Write(zip,"xl/styles.xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Microsoft YaHei\"/></font><font><b/><sz val=\"11\"/><name val=\"Microsoft YaHei\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\" applyBorder=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\" applyBorder=\"1\" applyFont=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf></cellXfs></styleSheet>");

        int columns=grid.Count==0?0:grid.Max(r=>r.Length);
        var sheet=new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        // 按内容自适应列宽（中文按 2 个字符宽估算）
        sheet.Append("<cols>");
        for(var c=0;c<columns;c++)
        {
            double chars=8;
            for(var r=0;r<grid.Count;r++)
                if(c<grid[r].Length&&!string.IsNullOrEmpty(grid[r][c]))
                    chars=Math.Max(chars,grid[r][c].Sum(ch=>ch>0x2E80?2.0:1.0));
            var width=Math.Min(40.0,chars*1.15+2);
            sheet.Append($"<col min=\"{c+1}\" max=\"{c+1}\" width=\"{width:0.0}\" customWidth=\"1\"/>");
        }
        sheet.Append("</cols><sheetData>");
        for(var r=0;r<grid.Count;r++)
        {
            sheet.Append("<row r=\"").Append(r+1).Append("\" ht=\"22\" customHeight=\"1\">");
            for(var c=0;c<columns;c++)
            {
                var value=c<grid[r].Length?(grid[r][c]??""):"";
                var style=r==0?2:1;
                if(value.Length>0&&double.TryParse(value,out _))
                    sheet.Append($"<c r=\"{CellRef(c,r)}\" s=\"{style}\"><v>{value}</v></c>");
                else
                    sheet.Append($"<c r=\"{CellRef(c,r)}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(value)}</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");
        if(merges.Count>0)
        {
            sheet.Append($"<mergeCells count=\"{merges.Count}\">");
            foreach(var(r1,c1,r2,c2)in merges)
                sheet.Append($"<mergeCell ref=\"{CellRef(c1,r1)}:{CellRef(c2,r2)}\"/>");
            sheet.Append("</mergeCells>");
        }
        sheet.Append("</worksheet>");
        Write(zip,"xl/worksheets/sheet1.xml",sheet.ToString());
    }

    static string CellRef(int col,int row)
    {
        var name="";
        for(var c=col;c>=0;c=c/26-1)name=(char)('A'+c%26)+name;
        return name+(row+1);
    }

    static void Write(ZipArchive zip,string name,string content)
    {
        using var stream=zip.CreateEntry(name).Open();
        var bytes=new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes,0,bytes.Length);
    }
}
